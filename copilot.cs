#!/usr/bin/env dotnet run
#:package GitHub.Copilot.SDK@0.1.26
#:package ErrorOr@2.0.1
#:package Spectre.Console@0.49.1

using System.Diagnostics;
using System.ComponentModel;
using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using ErrorOr;
using GitHub.Copilot.SDK;
using Spectre.Console;

const string ConfigFileName = ".gitcopilot-config.json";
const int JsonFileBufferSize = 16 * 1024;
ConsoleCancelCoordinator.Initialize();

var globalCancellationToken = ConsoleCancelCoordinator.Token;

var optionsResult = ParseArguments(args);
if (optionsResult.IsError)
{
    WriteError($"Argument Error: {optionsResult.FirstError.Description}");
    PrintUsage();
    return;
}

var options = optionsResult.Value;

if (options.ShowHelp)
{
    PrintUsage();
    return;
}

if (!options.ReplMode && string.IsNullOrWhiteSpace(options.Prompt))
{
    WriteWarning("Prompt is required.");
    PrintUsage();
    return;
}

var configResult = await LoadConfiguration(globalCancellationToken);
if (configResult.IsError)
{
    WriteError($"Configuration Error: {configResult.FirstError.Description}");
    return;
}

var config = configResult.Value;
var configValidationResult = ValidateConfiguration(config);
if (configValidationResult.IsError)
{
    WriteError($"Configuration Error: {configValidationResult.FirstError.Description}");
    return;
}

var runtimeConfig = BuildRuntimeConfig(config);
var profileName = options.ProfileName ?? runtimeConfig.DefaultProfile;

if (!runtimeConfig.Profiles.TryGetValue(profileName, out var profile))
{
    WriteError($"Profile '{profileName}' not found.");
    return;
}

if (options.ReplMode)
{
    await ExecuteGitCommitReplAsync(
        profileName,
        profile,
        options.Verbose,
        runtimeConfig.Git,
        runtimeConfig.Repl,
        runtimeConfig.GlobalMcpServers,
        globalCancellationToken);
    return;
}

await ExecuteAgentWorkflowAsync(
    profileName, 
    profile, 
    options.Prompt, 
    options.Verbose, 
    runtimeConfig.GlobalMcpServers, 
    globalCancellationToken);

// --- Core Functions ---

/// <summary>
/// Parses raw command-line arguments into a strongly typed options object.
/// </summary>
/// <param name="rawArgs">Raw CLI arguments passed to the script.</param>
/// <returns>A <see cref="CliOptions"/> instance when parsing succeeds, or an <see cref="ErrorOr{T}"/> validation error.</returns>
static ErrorOr<CliOptions> ParseArguments(string[] rawArgs)
{
    string? profileName = null;
    var positional = new List<string>();
    var verbose = false;
    var replMode = false;

    for (var index = 0; index < rawArgs.Length; index++)
    {
        var arg = rawArgs[index];
        switch (arg)
        {
            case "--help":
            case "-h":
                return new CliOptions(profileName, string.Empty, verbose, true, replMode);
            case "--verbose":
            case "-v":
                verbose = true;
                break;
            case "--repl":
            case "-i":
                replMode = true;
                break;
            case "--profile":
            case "-p":
                if (index + 1 >= rawArgs.Length)
                    return Error.Validation(description: "--profile requires a value.");

                profileName = rawArgs[++index];
                break;
            case "--":
                positional.AddRange(rawArgs.Skip(index + 1));
                index = rawArgs.Length;
                break;
            default:
                if (arg.StartsWith("-"))
                    return Error.Validation(description: $"Unknown flag '{arg}'.");

                positional.Add(arg);
                break;
        }
    }

    if (profileName is null && positional.Count >= 2)
    {
        profileName = positional[0];
        positional = [.. positional.Skip(1)];
    }

    var prompt = string.Join(" ", positional);
    return new CliOptions(profileName, prompt, verbose, false, replMode);
}

/// <summary>
/// Prints usage examples for one-shot and REPL invocation modes.
/// </summary>
static void PrintUsage()
{
    AnsiConsole.MarkupLine("[bold deepskyblue2]Usage[/]");
    AnsiConsole.WriteLine("  dotnet run copilot.cs -- [profile] \"Your prompt here\"");
    AnsiConsole.WriteLine("  dotnet run copilot.cs -- --profile <name> [--verbose] \"Your prompt here\"");
    AnsiConsole.WriteLine("  dotnet run copilot.cs -- --repl [--profile <name>] [--verbose]");
}

/// <summary>
/// Loads application configuration from the workspace-local config file, bootstrapping one if missing.
/// </summary>
/// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
/// <returns>A validated deserialized <see cref="AppConfig"/> payload, or an error describing the failure reason.</returns>
static async Task<ErrorOr<AppConfig>> LoadConfiguration(CancellationToken cancellationToken)
{
    var currentDirectory = Directory.GetCurrentDirectory();
    var repoRoot = await TryGetGitRepoRootAsync(currentDirectory, cancellationToken);

    string configPath;
    if (!string.IsNullOrWhiteSpace(repoRoot))
    {
        configPath = Path.Combine(repoRoot, ConfigFileName);
        if (!File.Exists(configPath))
        {
            var defaultConfig = CreateDefaultConfig();
            await WriteJsonFileAsync(configPath, defaultConfig, AppConfigSerializerContext.Default.AppConfig, cancellationToken);
            WriteInfo($"Created default config at {configPath}");
        }
    }
    else
    {
        configPath = Path.Combine(currentDirectory, ConfigFileName);
        if (!File.Exists(configPath))
            return Error.NotFound(description: $"Config not found at {configPath}. Run inside a git repository to auto-bootstrap config.");
    }

    try
    {
        var config = await ReadJsonFileAsync(configPath, AppConfigSerializerContext.Default.AppConfig, cancellationToken);
        return config is not null ? config : Error.Validation(description: "Invalid JSON configuration.");
    }
    catch (JsonException ex)
    {
        return Error.Validation(description: $"JSON Parsing failed: {ex.Message}");
    }
}

/// <summary>
/// Generates the default application configuration template.
/// </summary>
/// <returns>An instance of <see cref="AppConfig"/> with default profiles and settings.</returns>
static AppConfig CreateDefaultConfig()
{
    var profiles = new Dictionary<string, ProviderProfile>(StringComparer.OrdinalIgnoreCase)
    {
        ["cloud"] = new ProviderProfile("GitHub", null, null, null, 60, null, null),
        ["lmstudio"] = new ProviderProfile("OpenAICompatible", "http://localhost:1234/v1", "lm-studio", "openai/gpt-oss-20b", 300, 30, null)
    };

    return new AppConfig(
        profiles,
        "cloud",
        new GitToolConfig(),
        new ReplConfig(),
        null);
}

/// <summary>
/// Attempts to find the root directory of the current git repository.
/// </summary>
/// <param name="workingDirectory">The directory to start the search from.</param>
/// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
/// <returns>The absolute path to the git root, or null if not inside a repository.</returns>
static async Task<string?> TryGetGitRepoRootAsync(string workingDirectory, CancellationToken cancellationToken)
{
    var gitRootResult = await RunGitCommandAsync(workingDirectory, cancellationToken, "rev-parse", "--show-toplevel");
    if (gitRootResult.IsError)
        return null;

    if (gitRootResult.Value.ExitCode != 0 || string.IsNullOrWhiteSpace(gitRootResult.Value.StdOut))
        return null;

    return Path.GetFullPath(gitRootResult.Value.StdOut.Trim());
}

static void WriteInfo(string message) => AnsiConsole.MarkupLine($"[grey]{Markup.Escape(message)}[/]");
static void WriteWarning(string message) => AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(message)}[/]");
static void WriteError(string message) => AnsiConsole.MarkupLine($"[red]{Markup.Escape(message)}[/]");
static void WriteSuccess(string message) => AnsiConsole.MarkupLine($"[green]{Markup.Escape(message)}[/]");

static async Task<T?> ReadJsonFileAsync<T>(string filePath, JsonTypeInfo<T> jsonTypeInfo, CancellationToken cancellationToken)
{
    await using var readStream = new FileStream(
        filePath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize: JsonFileBufferSize,
        options: FileOptions.Asynchronous | FileOptions.SequentialScan);

    return await JsonSerializer.DeserializeAsync(readStream, jsonTypeInfo, cancellationToken);
}

static async Task WriteJsonFileAsync<T>(string filePath, T value, JsonTypeInfo<T> jsonTypeInfo, CancellationToken cancellationToken)
{
    await using var writeStream = new FileStream(
        filePath,
        FileMode.Create,
        FileAccess.Write,
        FileShare.None,
        bufferSize: JsonFileBufferSize,
        options: FileOptions.Asynchronous | FileOptions.SequentialScan);

    await JsonSerializer.SerializeAsync(writeStream, value, jsonTypeInfo, cancellationToken);
}

static RuntimeConfigView BuildRuntimeConfig(AppConfig config)
{
    var profiles = config.Profiles.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    var globalMcpServers = (config.McpServers ?? new Dictionary<string, McpServerConfig>(StringComparer.OrdinalIgnoreCase))
        .ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    return new RuntimeConfigView(
        profiles,
        config.DefaultProfile,
        config.Git ?? new GitToolConfig(),
        config.Repl ?? new ReplConfig(),
        globalMcpServers);
}

/// <summary>
/// Validates configuration invariants before any provider or session logic executes.
/// </summary>
/// <param name="config">Configuration object loaded from JSON.</param>
/// <returns><see cref="Result.Success"/> on valid config; otherwise descriptive validation errors.</returns>
static ErrorOr<Success> ValidateConfiguration(AppConfig config)
{
    if (config.Profiles is null || config.Profiles.Count == 0)
        return Error.Validation(description: "At least one profile is required in Profiles.");

    if (string.IsNullOrWhiteSpace(config.DefaultProfile))
        return Error.Validation(description: "DefaultProfile is required.");

    if (!config.Profiles.ContainsKey(config.DefaultProfile))
        return Error.Validation(description: $"DefaultProfile '{config.DefaultProfile}' does not exist in Profiles.");

    var globalMcpValidationResult = ValidateMcpServers(config.McpServers, "global");
    if (globalMcpValidationResult.IsError)
        return globalMcpValidationResult.FirstError;

    foreach (var (profileName, profile) in config.Profiles)
    {
        var providerTypeResult = ResolveProviderType(profile.ProviderType);
        if (providerTypeResult.IsError)
            return Error.Validation(description: $"Profile '{profileName}': {providerTypeResult.FirstError.Description}");

        if (providerTypeResult.Value == "openai")
        {
            if (string.IsNullOrWhiteSpace(profile.BaseUrl))
                return Error.Validation(description: $"Profile '{profileName}': BaseUrl is required for OpenAI-compatible providers.");

            if (!Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var parsedUri)
                || (parsedUri.Scheme != Uri.UriSchemeHttp && parsedUri.Scheme != Uri.UriSchemeHttps))
            {
                return Error.Validation(description: $"Profile '{profileName}': BaseUrl must be a valid HTTP/HTTPS URL.");
            }
        }

        var profileMcpValidationResult = ValidateMcpServers(profile.McpServers, $"profile '{profileName}'");
        if (profileMcpValidationResult.IsError)
            return profileMcpValidationResult.FirstError;
    }

    return Result.Success;
}

/// <summary>
/// Validates a collection of Model Context Protocol (MCP) server configurations.
/// </summary>
/// <param name="servers">Dictionary of server configurations to validate.</param>
/// <param name="scope">A descriptive string of where these servers are defined (e.g., "global" or "profile 'cloud'").</param>
/// <returns><see cref="Result.Success"/> if valid, otherwise a validation error.</returns>
static ErrorOr<Success> ValidateMcpServers(IReadOnlyDictionary<string, McpServerConfig>? servers, string scope)
{
    if (servers is null)
        return Result.Success;

    foreach (var (name, server) in servers)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Error.Validation(description: $"MCP server name in {scope} must not be empty.");

        var hasUrl = !string.IsNullOrWhiteSpace(server.Url);
        var hasCommand = !string.IsNullOrWhiteSpace(server.Command);
        if (!hasUrl && !hasCommand)
            return Error.Validation(description: $"MCP server '{name}' in {scope} must define either Url or Command.");
    }

    return Result.Success;
}

/// <summary>
/// Runs the interactive GitCommit shell.
/// </summary>
/// <param name="profileName">The configuration key of the active provider profile.</param>
/// <param name="profile">The loaded provider profile containing URL and API settings.</param>
/// <param name="verbose">Whether to print extended diagnostic output.</param>
/// <param name="gitConfig">Git policy and template configuration.</param>
/// <param name="replConfig">REPL bounds and presentation settings.</param>
/// <param name="globalMcpServers">Globally configured MCP servers to attach to the session.</param>
/// <param name="cancellationToken">Global token to monitor for application termination.</param>
static async Task ExecuteGitCommitReplAsync(
    string profileName,
    ProviderProfile profile,
    bool verbose,
    GitToolConfig gitConfig,
    ReplConfig replConfig,
    IReadOnlyDictionary<string, McpServerConfig>? globalMcpServers,
    CancellationToken cancellationToken)
{
    var repoRootResult = await EnsureRunningAtGitRepoRootAsync(cancellationToken);
    if (repoRootResult.IsError)
    {
        WriteError($"Git Error: {repoRootResult.FirstError.Description}");
        return;
    }

    var repoRoot = repoRootResult.Value;
    var cacheStore = await LoadCommitMessageCacheAsync(repoRoot, gitConfig, cancellationToken);
    var branchMemoryStore = await LoadBranchMemoryStoreAsync(repoRoot, gitConfig, cancellationToken);
    var dryRunMode = gitConfig.DryRunDefault;
    var activeProfile = profile;

    var sessionConfigResult = await BuildSessionConfigAsync(profileName, activeProfile, globalMcpServers, showRoutingBanner: true, cancellationToken);
    if (sessionConfigResult.IsError)
    {
        WriteError($"Configuration Error: {sessionConfigResult.FirstError.Description}");
        return;
    }

    var sessionConfig = sessionConfigResult.Value;

    await using var client = new CopilotClient();
    try
    {
        await client.StartAsync(cancellationToken);
    }
    catch (Exception ex)
    {
        WriteError($"Failed to start Copilot client: {ex.Message}");
        return;
    }

    try
    {
        var activeModel = string.IsNullOrWhiteSpace(sessionConfig.Model) ? "default" : sessionConfig.Model;
        PrintReplStartupBanner(repoRoot, profileName, activeModel, dryRunMode, cacheStore.Entries.Count);

        var currentBranch = await GetCurrentBranchNameAsync(repoRoot, cancellationToken);
        var sessionScopeKey = BuildSessionScopeKey(repoRoot, currentBranch);
        var scopedSessions = new Dictionary<string, CopilotSession>(StringComparer.OrdinalIgnoreCase);
        var session = await GetOrCreateScopedSessionAsync(scopedSessions, sessionScopeKey, client, sessionConfig, cancellationToken);
        string? styleHint = null;
        AttachedContext? attachedContext = null;
        string? lastGeneratedCommitMessage = null;
        string? lastCommitAnalysisSummary = null;

        if (verbose) WriteInfo("[REPL session created]");

        try
        {
            var commandHandlers = new Dictionary<ReplCommand, Func<string, Task<ReplLoopControl>>>
            {
                [ReplCommand.Help] = async _ => { PrintReplHelp(); return ReplLoopControl.Continue; },
                [ReplCommand.Exit] = async _ => ReplLoopControl.Exit,
                [ReplCommand.Status] = async _ =>
                {
                    await PrintGitCommandOutputAsync(repoRoot, cancellationToken, ["status", "--short", "--branch"]);
                    return ReplLoopControl.Continue;
                },
                [ReplCommand.Diff] = async arg =>
                {
                    await HandleDiffCommandAsync(repoRoot, arg, replConfig.MaxDiffCharacters, cancellationToken);
                    return ReplLoopControl.Continue;
                },
                [ReplCommand.Add] = async arg =>
                {
                    await HandleAddCommandAsync(repoRoot, arg, dryRunMode, cancellationToken);
                    return ReplLoopControl.Continue;
                },
                [ReplCommand.Reset] = async arg =>
                {
                    await HandleResetCommandAsync(repoRoot, arg, dryRunMode, cancellationToken);
                    return ReplLoopControl.Continue;
                },
                [ReplCommand.Commit] = async arg =>
                {
                    var commitOptions = ParseCommitOptions(arg);
                    var commitResult = await HandleCommitCommandAsync(
                        session, activeProfile, repoRoot, gitConfig, replConfig, styleHint, attachedContext,
                        amend: false, verbose: verbose, dryRun: dryRunMode, cacheStore: cacheStore, branchMemoryStore: branchMemoryStore, commitOptions: commitOptions, cancellationToken);
                    if (commitResult is not null)
                    {
                        lastGeneratedCommitMessage = commitResult.Message;
                        lastCommitAnalysisSummary = commitResult.AnalysisSummary;
                    }

                    styleHint = null;
                    attachedContext = null;
                    return ReplLoopControl.Continue;
                },
                [ReplCommand.Amend] = async arg =>
                {
                    var commitOptions = ParseCommitOptions(arg);
                    var commitResult = await HandleCommitCommandAsync(
                        session, activeProfile, repoRoot, gitConfig, replConfig, styleHint, attachedContext,
                        amend: true, verbose: verbose, dryRun: dryRunMode, cacheStore: cacheStore, branchMemoryStore: branchMemoryStore, commitOptions: commitOptions, cancellationToken);
                    if (commitResult is not null)
                    {
                        lastGeneratedCommitMessage = commitResult.Message;
                        lastCommitAnalysisSummary = commitResult.AnalysisSummary;
                    }

                    styleHint = null;
                    attachedContext = null;
                    return ReplLoopControl.Continue;
                },
                [ReplCommand.Push] = async arg =>
                {
                    var parsed = SplitArguments(arg);
                    var remote = parsed.Count >= 1 ? parsed[0] : null;
                    var branch = parsed.Count >= 2 ? parsed[1] : null;
                    var pushResult = await ExecutePushAsync(repoRoot, gitConfig, remote, branch, initiatedByAutoPush: false, dryRun: dryRunMode, cancellationToken);
                    if (pushResult.IsError)
                    {
                        WriteError($"Push Error: {pushResult.FirstError.Description}");
                        await PrintRecoverySuggestionAsync(session, activeProfile, "git push", pushResult.FirstError.Description, cancellationToken);
                    }

                    return ReplLoopControl.Continue;
                },
                [ReplCommand.DryRun] = async arg =>
                {
                    dryRunMode = ResolveDryRunCommand(arg, dryRunMode);
                    WriteInfo($"Dry-run mode is now {(dryRunMode ? "ON" : "OFF")}.");
                    return ReplLoopControl.Continue;
                },
                [ReplCommand.Cache] = async arg =>
                {
                    await HandleCacheCommandAsync(cacheStore, arg, cancellationToken);
                    return ReplLoopControl.Continue;
                },
                [ReplCommand.Model] = async _ =>
                {
                    var selectedModelResult = await SelectModelInteractivelyAsync(activeProfile, cancellationToken);
                    if (selectedModelResult.IsError)
                    {
                        WriteError($"Model Error: {selectedModelResult.FirstError.Description}");
                        return ReplLoopControl.Continue;
                    }

                    var selectedModel = selectedModelResult.Value;
                    activeProfile = activeProfile with { Model = selectedModel };

                    var rebuildResult = await BuildSessionConfigAsync(profileName, activeProfile, globalMcpServers, showRoutingBanner: true, cancellationToken);
                    if (rebuildResult.IsError)
                    {
                        WriteError($"Model Error: {rebuildResult.FirstError.Description}");
                        return ReplLoopControl.Continue;
                    }

                    foreach (var scoped in scopedSessions.Values)
                        await scoped.DisposeAsync();
                    scopedSessions.Clear();

                    session = await GetOrCreateScopedSessionAsync(scopedSessions, sessionScopeKey, client, rebuildResult.Value, cancellationToken);
                    WriteSuccess($"Active model switched to: {selectedModel}");
                    return ReplLoopControl.Continue;
                },
                [ReplCommand.Undo] = async _ =>
                {
                    await HandleUndoCommandAsync(repoRoot, gitConfig, dryRunMode, cancellationToken);
                    return ReplLoopControl.Continue;
                },
                [ReplCommand.Log] = async arg =>
                {
                    await HandleLogCommandAsync(repoRoot, arg, cancellationToken);
                    return ReplLoopControl.Continue;
                },
                [ReplCommand.Attach] = async arg =>
                {
                    var attachResult = await HandleAttachCommandAsync(repoRoot, arg, replConfig.MaxAttachmentCharacters, cancellationToken);
                    if (attachResult.IsError)
                    {
                        WriteError($"Attach Error: {attachResult.FirstError.Description}");
                        return ReplLoopControl.Continue;
                    }

                    attachedContext = attachResult.Value;
                    if (attachedContext is null)
                        WriteInfo("Attachment cleared.");
                    else
                        WriteSuccess($"Attached context from: {attachedContext.Path} (applies to next /commit or /amend)");

                    return ReplLoopControl.Continue;
                },
                [ReplCommand.Pr] = async arg =>
                {
                    await HandlePrCommandAsync(session, activeProfile, repoRoot, gitConfig, replConfig, arg, cancellationToken);
                    return ReplLoopControl.Continue;
                },
                [ReplCommand.Review] = async arg =>
                {
                    await HandleReviewCommandAsync(session, activeProfile, repoRoot, replConfig, arg, cancellationToken);
                    return ReplLoopControl.Continue;
                },
                [ReplCommand.Refine] = async arg =>
                {
                    if (string.IsNullOrWhiteSpace(lastGeneratedCommitMessage))
                    {
                        WriteWarning("No generated commit message to refine yet. Run /commit or /amend first.");
                        return ReplLoopControl.Continue;
                    }

                    var refined = await RefineCommitMessageAsync(session, activeProfile, lastGeneratedCommitMessage, arg, cancellationToken);
                    if (refined.IsError)
                    {
                        WriteError($"Refine Error: {refined.FirstError.Description}");
                        return ReplLoopControl.Continue;
                    }

                    lastGeneratedCommitMessage = refined.Value;
                    AnsiConsole.Write(new Panel(new Markup(Markup.Escape(refined.Value)))
                        .Header("[deepskyblue2]Refined Commit Message[/]")
                        .Border(BoxBorder.Rounded)
                        .Expand());

                    if (!string.IsNullOrWhiteSpace(lastCommitAnalysisSummary))
                        WriteInfo($"Last analysis context: {lastCommitAnalysisSummary}");

                    return ReplLoopControl.Continue;
                }
            }.ToFrozenDictionary();

            while (!cancellationToken.IsCancellationRequested)
            {
                AnsiConsole.Markup($"[deepskyblue2]{Markup.Escape(replConfig.Prompt)}[/]");
                var input = Console.ReadLine();
                if (input is null) break;

                input = input.Trim();
                if (string.IsNullOrWhiteSpace(input)) continue;

                if (!input.StartsWith("/"))
                {
                    styleHint = input;
                    WriteInfo("Saved style hint for next commit generation. Use /commit or /amend.");
                    continue;
                }

                var parsed = ParseReplCommand(input);
                if (parsed.Command == ReplCommand.Unknown)
                {
                    WriteWarning($"Unknown command '{parsed.RawCommand}'. Use /help.");
                    continue;
                }

                if (!commandHandlers.TryGetValue(parsed.Command, out var commandHandler))
                {
                    WriteWarning($"Unknown command '{parsed.RawCommand}'. Use /help.");
                    continue;
                }

                var control = await commandHandler(parsed.ArgText);
                if (control == ReplLoopControl.Exit) return;
            }
        }
        finally
        {
            foreach (var scoped in scopedSessions.Values)
                await scoped.DisposeAsync();
        }
    }
    catch (OperationCanceledException)
    {
        WriteInfo("\nREPL session terminated by user.");
    }
    catch (Exception ex)
    {
        WriteError($"REPL failed: {ex.Message}");
    }
}

/// <summary>
/// Prints a compact, high-signal REPL startup line indicating repository and profile state.
/// </summary>
/// <param name="repoRoot">Absolute repository root path.</param>
/// <param name="profileName">Active provider profile configuration key.</param>
/// <param name="activeModel">Active model identifier.</param>
/// <param name="dryRunMode">Whether mutating commands are currently simulated.</param>
/// <param name="cacheEntries">Current cached message count.</param>
static void PrintReplStartupBanner(string repoRoot, string profileName, string activeModel, bool dryRunMode, int cacheEntries)
{
    var repoName = Path.GetFileName(repoRoot.TrimEnd(Path.DirectorySeparatorChar));
    AnsiConsole.Write(new Rule($"[deepskyblue2]GitCopilot[/] [grey]•[/] [white]{Markup.Escape(repoName)}[/]"));
    AnsiConsole.MarkupLine($"[grey]profile[/]=[white]{Markup.Escape(profileName)}[/]  [grey]model[/]=[white]{Markup.Escape(activeModel)}[/]  [grey]dry-run[/]=[white]{(dryRunMode ? "on" : "off")}[/]  [grey]cache[/]=[white]{cacheEntries}[/]");
    AnsiConsole.MarkupLine("[grey]Type /help for commands. Type /exit to quit.[/]\n");
}

/// <summary>
/// Displays available remote models from a local API and lets the user pick one interactively.
/// </summary>
/// <param name="profile">Current provider profile used for model discovery.</param>
/// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
/// <returns>The selected model ID, or an error when model discovery/selection fails.</returns>
static async Task<ErrorOr<string>> SelectModelInteractivelyAsync(ProviderProfile profile, CancellationToken cancellationToken)
{
    var providerTypeResult = ResolveProviderType(profile.ProviderType);
    if (providerTypeResult.IsError)
        return providerTypeResult.FirstError;

    if (providerTypeResult.Value != "openai")
        return Error.Validation(description: "/model is currently supported for OpenAI-compatible profiles only.");

    if (string.IsNullOrWhiteSpace(profile.BaseUrl))
        return Error.Validation(description: "BaseUrl is required to load available models.");

    var modelsResult = await FetchAvailableModelsAsync(profile.BaseUrl, profile.ApiKey, ResolveModelDiscoveryTimeout(profile), cancellationToken);
    if (modelsResult.IsError)
        return modelsResult.FirstError;

    var models = modelsResult.Value;
    if (models.Count == 0)
        return Error.NotFound(description: "No models returned by provider /models endpoint.");

    var selected = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title("[deepskyblue2]Select active model[/]")
            .PageSize(Math.Clamp(models.Count, 3, 15))
            .AddChoices(models));

    return selected;
}

/// <summary>
/// Executes the end-to-end commit flow: gathers context, requests inference, confirms payload, executes git commit, and optionally pushes.
/// </summary>
/// <param name="session">Active Copilot session used for message generation.</param>
/// <param name="profile">Provider profile controlling model and timeout behavior.</param>
/// <param name="repoRoot">Repository root used for all git command execution.</param>
/// <param name="gitConfig">Git safety, cache, and policy settings.</param>
/// <param name="replConfig">REPL bounds for truncating oversized diffs.</param>
/// <param name="styleHint">Optional user style guidance from free-form REPL input.</param>
/// <param name="attachedContext">Optional one-shot attached file context for this generation attempt.</param>
/// <param name="amend">When true, generates a message for an amend flow instead of a new commit.</param>
/// <param name="verbose">Enables extra diagnostic output during generation.</param>
/// <param name="dryRun">Skips git mutations while echoing intended commands to stdout.</param>
/// <param name="cacheStore">Commit message cache store for deduplication.</param>
/// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
static async Task<CommitCommandResult?> HandleCommitCommandAsync(
    CopilotSession session,
    ProviderProfile profile,
    string repoRoot,
    GitToolConfig gitConfig,
    ReplConfig replConfig,
    string? styleHint,
    AttachedContext? attachedContext,
    bool amend,
    bool verbose,
    bool dryRun,
    CommitMessageCacheStore cacheStore,
    BranchMemoryStore branchMemoryStore,
    CommitRequestOptions commitOptions,
    CancellationToken cancellationToken)
{
    var hasHeadResult = await RunGitCommandAsync(repoRoot, cancellationToken, "rev-parse", "--verify", "HEAD");
    if (hasHeadResult.IsError)
    {
        WriteError($"Git Error: {hasHeadResult.FirstError.Description}");
        return null;
    }

    if (amend && hasHeadResult.Value.ExitCode != 0)
    {
        WriteWarning("Cannot amend because this repository has no commits yet.");
        return null;
    }

    var stagedDiff = await RunGitCommandAsync(repoRoot, cancellationToken, "diff", "--cached", "--");
    if (stagedDiff.IsError)
    {
        WriteError($"Git Error: {stagedDiff.FirstError.Description}");
        return null;
    }

    var hasStagedChanges = !string.IsNullOrWhiteSpace(stagedDiff.Value.StdOut);

    if (!amend && !hasStagedChanges)
    {
        WriteWarning("No staged changes found. Use /add first.");
        return null;
    }

    var contextDiff = stagedDiff.Value.StdOut;
    if (!hasStagedChanges)
    {
        var showResult = await RunGitCommandAsync(repoRoot, cancellationToken, "show", "--no-color", "--patch", "--stat", "-1");
        if (showResult.IsError)
        {
            WriteError($"Git Error: {showResult.FirstError.Description}");
            return null;
        }

        contextDiff = showResult.Value.StdOut;
    }

    var statusResult = await RunGitCommandAsync(repoRoot, cancellationToken, "status", "--short", "--branch");
    if (statusResult.IsError)
    {
        WriteError($"Git Error: {statusResult.FirstError.Description}");
        return null;
    }

    var status = statusResult.Value.StdOut;
    var branch = ParseBranchFromStatus(status);
    if (string.IsNullOrWhiteSpace(branch))
    {
        var branchResult = await RunGitCommandAsync(repoRoot, cancellationToken, "rev-parse", "--abbrev-ref", "HEAD");
        branch = !branchResult.IsError && branchResult.Value.ExitCode == 0
            ? branchResult.Value.StdOut.Trim()
            : "unknown";
    }

    var sessionScopeKey = BuildSessionScopeKey(repoRoot, branch);
    branchMemoryStore.Entries.TryGetValue(sessionScopeKey, out var branchMemory);
    var effectiveStyleHint = !string.IsNullOrWhiteSpace(styleHint)
        ? styleHint
        : branchMemory?.PreferredStyleHint;

    var cacheKey = ComputeCommitDiffKey(status, contextDiff, branch, amend, effectiveStyleHint, gitConfig, commitOptions.Quality);

    ErrorOr<CommitGenerationResult> messageResult;
    if (gitConfig.EnableCommitMessageCache && cacheStore.Entries.TryGetValue(cacheKey, out var cached))
    {
        if (verbose) WriteInfo("[Commit cache hit]");
        messageResult = new CommitGenerationResult(cached.Message, null, null);
    }
    else
    {
        var truncatedDiff = TruncateForPrompt(contextDiff, replConfig.MaxDiffCharacters);
        var isDiffTruncated = contextDiff.Length > truncatedDiff.Length;
        messageResult = await GenerateCommitMessageAsync(
            session, profile, status, truncatedDiff, gitConfig, effectiveStyleHint, branch, attachedContext, amend, commitOptions, branchMemory, isDiffTruncated, verbose, cancellationToken);

        if (!messageResult.IsError && gitConfig.EnableCommitMessageCache)
        {
            cacheStore.Entries[cacheKey] = new CommitMessageCacheEntry(
                messageResult.Value.Message,
                DateTime.UtcNow.ToString("O"),
                branch,
                amend);

            var saveResult = await SaveCommitMessageCacheAsync(cacheStore, cancellationToken);
            if (saveResult.IsError)
            {
                WriteWarning($"Warning: could not persist cache. {saveResult.FirstError.Description}");
            }
            else if (!cacheStore.GitIgnoreCheckCompleted)
            {
                var ensureIgnoreResult = await EnsureCacheFileIgnoredOnceAsync(cacheStore, cancellationToken);
                if (ensureIgnoreResult.IsError)
                {
                    WriteWarning($"Warning: cache ignore setup failed. {ensureIgnoreResult.FirstError.Description}");
                }
                else
                {
                    cacheStore.GitIgnoreCheckCompleted = true;
                    var markerSaveResult = await SaveCommitMessageCacheAsync(cacheStore, cancellationToken);
                    if (markerSaveResult.IsError)
                        WriteWarning($"Warning: could not persist cache metadata. {markerSaveResult.FirstError.Description}");
                }
            }
        }
    }

    if (messageResult.IsError)
    {
        WriteError($"Commit Message Error: {messageResult.FirstError.Description}");
        return null;
    }

    var generated = messageResult.Value;
    var message = generated.Message;

    if (generated.Analysis is not null)
    {
        var analysis = generated.Analysis;
        var summary = $"intent: {analysis.Intent}; risk: {analysis.Risks}; scope: {analysis.Scope}";
        WriteInfo($"Analysis: {summary}");

        if (!string.IsNullOrWhiteSpace(analysis.SuggestedActions))
        {
            AnsiConsole.Write(new Panel(new Markup(Markup.Escape(analysis.SuggestedActions)))
                .Header("[deepskyblue2]Suggested Pre-Commit Actions[/]")
                .Border(BoxBorder.Rounded)
                .Expand());

            if (gitConfig.RequireConfirmations && !AskForConfirmation("Proceed without applying suggested actions? [y/N]: "))
            {
                WriteWarning("Commit canceled.");
                return null;
            }
        }
    }

    AnsiConsole.Write(new Panel(new Markup(Markup.Escape(message)))
        .Header("[deepskyblue2]Proposed Commit Message[/]")
        .Border(BoxBorder.Rounded)
        .Expand());
    AnsiConsole.WriteLine();

    if (gitConfig.RequireConfirmations && !AskForConfirmation(amend ? "Apply amend now? [y/N]: " : "Create commit now? [y/N]: "))
    {
        WriteWarning("Commit canceled.");
        return null;
    }

    if (dryRun)
    {
        WriteInfo($"[dry-run] Would run: git commit {(amend ? "--amend " : string.Empty)}--file <temp-message-file>");
        if (gitConfig.AutoPush)
        {
            var pushResult = await ExecutePushAsync(repoRoot, gitConfig, null, null, initiatedByAutoPush: true, dryRun: true, cancellationToken);
            if (pushResult.IsError)
            {
                WriteError($"Auto-push Error: {pushResult.FirstError.Description}");
                await PrintRecoverySuggestionAsync(session, profile, "git push", pushResult.FirstError.Description, cancellationToken);
            }
        }

        return new CommitCommandResult(message, generated.Analysis is null ? null : generated.Analysis.Intent);
    }

    var commitResult = await CommitWithMessageAsync(repoRoot, message, amend, cancellationToken);
    if (commitResult.IsError)
    {
        WriteError($"Commit Error: {commitResult.FirstError.Description}");
        await PrintRecoverySuggestionAsync(session, profile, "git commit", commitResult.FirstError.Description, cancellationToken);
        return null;
    }

    WriteSuccess(amend ? "Commit amended successfully." : "Commit created successfully.");

    branchMemoryStore.Entries[sessionScopeKey] = new BranchMemoryEntry(
        effectiveStyleHint,
        generated.Schema?.Type ?? branchMemory?.LastCommitType,
        generated.Schema?.Scope ?? branchMemory?.LastCommitScope,
        DateTime.UtcNow.ToString("O"));

    var memorySaveResult = await SaveBranchMemoryStoreAsync(branchMemoryStore, cancellationToken);
    if (memorySaveResult.IsError)
        WriteWarning($"Warning: Could not persist branch memory. {memorySaveResult.FirstError.Description}");

    if (gitConfig.AutoPush)
    {
        var pushResult = await ExecutePushAsync(repoRoot, gitConfig, null, null, initiatedByAutoPush: true, dryRun: false, cancellationToken);
        if (pushResult.IsError)
        {
            WriteError($"Auto-push Error: {pushResult.FirstError.Description}");
            await PrintRecoverySuggestionAsync(session, profile, "git push", pushResult.FirstError.Description, cancellationToken);
        }
    }

    return new CommitCommandResult(message, generated.Analysis is null ? null : generated.Analysis.Intent);
}

/// <summary>
/// Packages the diff context into a prompt payload and requests inference from the language model.
/// </summary>
/// <param name="session">Active Copilot session.</param>
/// <param name="profile">Profile containing timeout and provider settings.</param>
/// <param name="status">The result of `git status --short --branch`.</param>
/// <param name="diff">Bounded diff text used as primary generation context.</param>
/// <param name="gitConfig">Formatting and policy preferences for commit messages.</param>
/// <param name="styleHint">Optional user-provided style hint.</param>
/// <param name="branch">Current branch name used in prompt context.</param>
/// <param name="attachedContext">Optional one-shot attached file context.</param>
/// <param name="amend">Whether the operation is an amend or new commit.</param>
/// <param name="verbose">Enables additional diagnostics output.</param>
/// <param name="cancellationToken">Token to cancel the SDK request.</param>
/// <returns>The generated, formatted commit message text.</returns>
static async Task<ErrorOr<CommitGenerationResult>> GenerateCommitMessageAsync(
    CopilotSession session,
    ProviderProfile profile,
    string status,
    string diff,
    GitToolConfig gitConfig,
    string? styleHint,
    string branch,
    AttachedContext? attachedContext,
    bool amend,
    CommitRequestOptions commitOptions,
    BranchMemoryEntry? branchMemory,
    bool isDiffTruncated,
    bool verbose,
    CancellationToken cancellationToken)
{
    var requestTimeout = ResolveRequestTimeout(profile);

    var analysisResult = await GenerateCommitAnalysisAsync(
        session,
        profile,
        status,
        diff,
        styleHint,
        branch,
        attachedContext,
        amend,
        commitOptions,
        branchMemory,
        isDiffTruncated,
        requestTimeout);

    if (analysisResult.IsError)
        return analysisResult.FirstError;

    var analysis = analysisResult.Value;
    var schemaResult = await GenerateCommitSchemaAsync(
        session,
        profile,
        status,
        diff,
        gitConfig,
        styleHint,
        branch,
        attachedContext,
        amend,
        commitOptions,
        branchMemory,
        analysis,
        isDiffTruncated,
        requestTimeout,
        cancellationToken);

    if (schemaResult.IsError)
    {
        var fallbackPrompt = BuildCommitFallbackPrompt(
            status,
            diff,
            gitConfig,
            styleHint,
            branch,
            attachedContext,
            amend,
            commitOptions,
            branchMemory,
            analysis,
            isDiffTruncated);

        try
        {
            var fallbackResponse = await session.SendAndWaitAsync(new MessageOptions
            {
                Prompt = fallbackPrompt
            }, timeout: requestTimeout);

            var fallbackContent = fallbackResponse?.Data?.Content;
            if (string.IsNullOrWhiteSpace(fallbackContent))
                return Error.Failure(description: "Model returned an empty message.");

            var fallbackFormatted = ApplyCommitFormatting(fallbackContent, gitConfig, branch);
            return new CommitGenerationResult(fallbackFormatted, analysis, null);
        }
        catch (OperationCanceledException)
        {
            return Error.Failure(description: "Operation canceled by user.");
        }
    }

    var schema = schemaResult.Value;
    var message = FormatCommitMessageFromSchema(schema, gitConfig, branch);
    if (verbose) WriteInfo("[Commit message generated | two-stage structured pipeline]");
    return new CommitGenerationResult(message, analysis, schema);
}

/// <summary>
/// Applies post-generation formatting rules such as conventional commit defaults, code block stripping, and custom project templates.
/// </summary>
/// <param name="raw">Raw output directly from the LLM.</param>
/// <param name="gitConfig">Repository-specific git formatting configuration.</param>
/// <param name="branch">Current active branch, used for template substitutions.</param>
/// <returns>A sanitized string ready for git ingestion.</returns>
static string ApplyCommitFormatting(string raw, GitToolConfig gitConfig, string branch)
{
    var message = raw.Trim();

    if (message.StartsWith("```", StringComparison.Ordinal))
    {
        var lines = message
            .Split('\n')
            .Where(line => !line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            .ToArray();
        message = string.Join("\n", lines).Trim();
    }

    if (gitConfig.UseConventionalCommits)
    {
        var lines = message.Split('\n').ToList();
        if (lines.Count > 0 && !RegexExtensions.SplitLineRegex().IsMatch(lines[0]))
            lines[0] = $"{gitConfig.DefaultCommitType}: {lines[0]}";

        message = string.Join("\n", lines).Trim();
    }

    if (!string.IsNullOrWhiteSpace(gitConfig.CustomTemplate))
    {
        var template = gitConfig.CustomTemplate;
        if (template.Contains("{{message}}", StringComparison.OrdinalIgnoreCase))
        {
            message = template
                .Replace("{{message}}", message, StringComparison.OrdinalIgnoreCase)
                .Replace("{{branch}}", branch, StringComparison.OrdinalIgnoreCase)
                .Replace("{{date}}", DateTime.UtcNow.ToString("yyyy-MM-dd"), StringComparison.OrdinalIgnoreCase)
                .Trim();
        }
        else
        {
            message = $"{template.Trim()}\n\n{message}";
        }
    }

    return message;
}

/// <summary>
/// Writes the final generated commit message to a temporary file and executes the git commit command.
/// </summary>
/// <param name="repoRoot">Absolute path to repository root.</param>
/// <param name="message">The validated text message to use for the commit.</param>
/// <param name="amend">If true, passes --amend to the git process.</param>
/// <param name="cancellationToken">Token to cancel background Git operations.</param>
/// <returns><see cref="Result.Success"/> if the commit succeeds.</returns>
static async Task<ErrorOr<Success>> CommitWithMessageAsync(string repoRoot, string message, bool amend, CancellationToken cancellationToken)
{
    var tempFile = Path.GetTempFileName();

    try
    {
        await File.WriteAllTextAsync(tempFile, message.Trim(), cancellationToken);

        string[] args = amend
            ? ["commit", "--amend", "--file", tempFile] 
            : ["commit", "--file", tempFile];

        var result = await RunGitCommandAsync(repoRoot, cancellationToken, args);
        if (result.IsError)
            return result.FirstError;

        if (result.Value.ExitCode != 0)
        {
            return Error.Failure(description: string.IsNullOrWhiteSpace(result.Value.StdErr)
                ? "git commit failed."
                : result.Value.StdErr.Trim());
        }

        if (!string.IsNullOrWhiteSpace(result.Value.StdOut))
            WriteInfo(result.Value.StdOut.Trim());

        return Result.Success;
    }
    finally
    {
        try { File.Delete(tempFile); } catch { }
    }
}

/// <summary>
/// Pushes the current branch to a remote target, optionally bypassing prompts if initiated by the AutoPush system.
/// </summary>
/// <param name="repoRoot">Absolute path to repository root.</param>
/// <param name="gitConfig">Repository git configuration governing branch protections.</param>
/// <param name="remote">Optional explicit remote target (e.g., origin).</param>
/// <param name="branch">Optional explicit branch target (e.g., main).</param>
/// <param name="initiatedByAutoPush">Flags whether this push was manually requested or triggered by the AutoPush flow.</param>
/// <param name="dryRun">If true, echoes the command without mutating network state.</param>
/// <param name="cancellationToken">Token to cancel the git push execution.</param>
/// <returns><see cref="Result.Success"/> if push succeeds.</returns>
static async Task<ErrorOr<Success>> ExecutePushAsync(
    string repoRoot,
    GitToolConfig gitConfig,
    string? remote,
    string? branch,
    bool initiatedByAutoPush,
    bool dryRun,
    CancellationToken cancellationToken)
{
    var branchResult = await RunGitCommandAsync(repoRoot, cancellationToken, "rev-parse", "--abbrev-ref", "HEAD");
    if (branchResult.IsError)
        return branchResult.FirstError;

    if (branchResult.Value.ExitCode != 0)
        return Error.Failure(description: "Unable to determine current branch before push.");

    var currentBranch = branchResult.Value.StdOut.Trim();
    if (gitConfig.ProtectedBranches.Any(b => string.Equals(b, currentBranch, StringComparison.OrdinalIgnoreCase)))
        return Error.Validation(description: $"Push blocked: branch '{currentBranch}' is protected.");

    if (gitConfig.RequireConfirmations)
    {
        var prompt = initiatedByAutoPush
            ? "Auto-push is enabled. Push now? [y/N]: "
            : "Push to remote now? [y/N]: ";

        if (!AskForConfirmation(prompt))
            return Error.Failure(description: "Push canceled.");
    }

    var pushArgs = new List<string> { "push" };
    if (!string.IsNullOrWhiteSpace(remote))
        pushArgs.Add(remote);
    if (!string.IsNullOrWhiteSpace(branch))
        pushArgs.Add(branch);

    if (dryRun)
    {
        WriteInfo($"[dry-run] Would run: git {string.Join(' ', pushArgs)}");
        return Result.Success;
    }

    var result = await RunGitCommandAsync(repoRoot, cancellationToken, [.. pushArgs]);
    if (result.IsError)
        return result.FirstError;

    if (result.Value.ExitCode != 0)
    {
        return Error.Failure(description: string.IsNullOrWhiteSpace(result.Value.StdErr)
            ? "git push failed."
            : result.Value.StdErr.Trim());
    }

    if (!string.IsNullOrWhiteSpace(result.Value.StdOut))
        WriteInfo(result.Value.StdOut.Trim());

    WriteSuccess("Push completed.");
    return Result.Success;
}

/// <summary>
/// Safely undoes the latest commit with a soft reset, preserving working tree changes while unwinding the commit history.
/// </summary>
/// <param name="repoRoot">Absolute path to repository root.</param>
/// <param name="gitConfig">Repository git configuration governing confirmation requirements.</param>
/// <param name="dryRun">If true, prevents the actual reset operation.</param>
/// <param name="cancellationToken">Token to cancel the background reset command.</param>
static async Task HandleUndoCommandAsync(string repoRoot, GitToolConfig gitConfig, bool dryRun, CancellationToken cancellationToken)
{
    if (gitConfig.RequireConfirmations && !AskForConfirmation("Undo last commit with soft reset (keep changes staged)? [y/N]: "))
    {
        WriteWarning("Undo canceled.");
        return;
    }

    if (dryRun)
    {
        WriteInfo("[dry-run] Would run: git reset --soft HEAD~1");
        return;
    }

    var result = await RunGitCommandAsync(repoRoot, cancellationToken, "reset", "--soft", "HEAD~1");
    if (result.IsError)
    {
        WriteError($"Undo Error: {result.FirstError.Description}");
        return;
    }

    if (result.Value.ExitCode != 0)
    {
        WriteError($"Undo Error: {result.Value.StdErr.Trim()}");
        return;
    }

    WriteSuccess("Undo completed: moved HEAD back one commit (soft). ");
}

/// <summary>
/// Displays staged or unstaged diff output, bounding exceptionally large changes to prevent console buffer overflow.
/// </summary>
/// <param name="repoRoot">Absolute path to repository root.</param>
/// <param name="argText">Raw argument string containing --staged or --unstaged flags.</param>
/// <param name="maxDiffCharacters">Truncation threshold for the rendered text panel.</param>
/// <param name="cancellationToken">Token to cancel background diff queries.</param>
static async Task HandleDiffCommandAsync(string repoRoot, string argText, int maxDiffCharacters, CancellationToken cancellationToken)
{
    var requested = argText.Trim();
    var explicitStaged = string.Equals(requested, "--staged", StringComparison.OrdinalIgnoreCase);
    var explicitUnstaged = string.Equals(requested, "--unstaged", StringComparison.OrdinalIgnoreCase);
    string[] args;

    if (explicitUnstaged)
        args = ["diff", "--"];
    else if (explicitStaged)
        args = ["diff", "--cached", "--"];
    else
    {
        var staged = await RunGitCommandAsync(repoRoot, cancellationToken, "diff", "--cached", "--");
        if (staged.IsError)
        {
            WriteError($"Diff Error: {staged.FirstError.Description}");
            return;
        }

        if (!string.IsNullOrWhiteSpace(staged.Value.StdOut))
        {
            RenderDiffOutput(staged.Value.StdOut, maxDiffCharacters);
            return;
        }

        args = ["diff", "--"];
    }

    var result = await RunGitCommandAsync(repoRoot, cancellationToken, [.. args]);
    if (result.IsError)
    {
        WriteError($"Diff Error: {result.FirstError.Description}");
        return;
    }

    if (result.Value.ExitCode != 0)
    {
        WriteError($"Diff Error: {result.Value.StdErr.Trim()}");
        return;
    }

    if (string.IsNullOrWhiteSpace(result.Value.StdOut))
    {
        if (explicitStaged)
        {
            var unstagedResult = await RunGitCommandAsync(repoRoot, cancellationToken, "diff", "--");
            if (!unstagedResult.IsError && unstagedResult.Value.ExitCode == 0 && !string.IsNullOrWhiteSpace(unstagedResult.Value.StdOut))
            {
                WriteInfo("No staged diff output. Unstaged changes exist; run /add or /diff --unstaged.");
                return;
            }

            WriteInfo("No staged diff output.");
            return;
        }

        if (explicitUnstaged)
        {
            var stagedResult = await RunGitCommandAsync(repoRoot, cancellationToken, "diff", "--cached", "--");
            if (!stagedResult.IsError && stagedResult.Value.ExitCode == 0 && !string.IsNullOrWhiteSpace(stagedResult.Value.StdOut))
            {
                WriteInfo("No unstaged diff output. Staged changes exist; run /diff --staged.");
                return;
            }

            WriteInfo("No unstaged diff output.");
            return;
        }

        WriteInfo("No diff output.");
        return;
    }

    RenderDiffOutput(result.Value.StdOut, maxDiffCharacters);
}

/// <summary>
/// Stages one or more files. When invoked without arguments, triggers an interactive multi-selection interface.
/// </summary>
/// <param name="repoRoot">Absolute path to repository root.</param>
/// <param name="argText">Raw argument string containing files to add.</param>
/// <param name="dryRun">If true, prints the add command without mutating the git index.</param>
/// <param name="cancellationToken">Token to cancel git add execution.</param>
static async Task HandleAddCommandAsync(string repoRoot, string argText, bool dryRun, CancellationToken cancellationToken)
{
    var paths = SplitArguments(argText);
    if (paths.Count == 0)
    {
        var unstagedFilesResult = await GetUnstagedFilesAsync(repoRoot, cancellationToken);
        if (unstagedFilesResult.IsError)
        {
            WriteError($"Add Error: {unstagedFilesResult.FirstError.Description}");
            return;
        }

        var unstagedFiles = unstagedFilesResult.Value;
        if (unstagedFiles.Count == 0)
        {
            WriteInfo("No unstaged files found.");
            return;
        }

        var selected = AnsiConsole.Prompt(
            new MultiSelectionPrompt<string>()
                .Title("[deepskyblue2]Select files to stage[/]")
                .PageSize(Math.Clamp(unstagedFiles.Count, 3, 15))
                .NotRequired()
                .AddChoices(unstagedFiles));

        if (selected.Count == 0)
        {
            WriteInfo("No files selected.");
            return;
        }

        paths.AddRange(selected);
    }

    var args = new List<string> { "add", "--" };
    args.AddRange(paths);

    if (dryRun)
    {
        WriteInfo($"[dry-run] Would run: git {string.Join(' ', args)}");
        return;
    }

    var result = await RunGitCommandAsync(repoRoot, cancellationToken, [.. args]);
    if (result.IsError)
    {
        WriteError($"Add Error: {result.FirstError.Description}");
        return;
    }

    if (result.Value.ExitCode != 0)
    {
        WriteError($"Add Error: {result.Value.StdErr.Trim()}");
        return;
    }

    WriteSuccess("Staging updated.");
}

/// <summary>
/// Unstages files from the index, preserving working tree modifications.
/// </summary>
/// <param name="repoRoot">Absolute path to repository root.</param>
/// <param name="argText">Raw argument string specifying which files to reset.</param>
/// <param name="dryRun">If true, prints the restore command without mutating git status.</param>
/// <param name="cancellationToken">Token to cancel background restore execution.</param>
static async Task HandleResetCommandAsync(string repoRoot, string argText, bool dryRun, CancellationToken cancellationToken)
{
    var paths = SplitArguments(argText);
    if (paths.Count == 0)
        paths.Add(".");

    var args = new List<string> { "restore", "--staged", "--" };
    args.AddRange(paths);

    if (dryRun)
    {
        WriteInfo($"[dry-run] Would run: git {string.Join(' ', args)}");
        return;
    }

    var result = await RunGitCommandAsync(repoRoot, cancellationToken, [.. args]);
    if (result.IsError)
    {
        WriteError($"Reset Error: {result.FirstError.Description}");
        return;
    }

    if (result.Value.ExitCode != 0)
    {
        WriteError($"Reset Error: {result.Value.StdErr.Trim()}");
        return;
    }

    WriteSuccess("Staging reset for selected paths.");
}

/// <summary>
/// Allocation-free argument tokenizer that splits shell strings while preserving internal quotes.
/// </summary>
/// <param name="argText">Raw string containing multiple arguments.</param>
/// <returns>A list of isolated token strings.</returns>
static List<string> SplitArguments(string argText)
{
    if (string.IsNullOrWhiteSpace(argText))
        return new List<string>();

    var result = new List<string>();
    ReadOnlySpan<char> span = argText.AsSpan();
    var index = 0;

    while (index < span.Length)
    {
        while (index < span.Length && char.IsWhiteSpace(span[index]))
            index++;

        if (index >= span.Length)
            break;

        var quoted = span[index] == '"';
        if (quoted)
            index++;

        var start = index;
        while (index < span.Length)
        {
            if (quoted)
            {
                if (span[index] == '"')
                    break;
            }
            else if (char.IsWhiteSpace(span[index]))
            {
                break;
            }

            index++;
        }

        var token = span[start..index].ToString().Trim();
        if (!string.IsNullOrWhiteSpace(token))
            result.Add(token);

        if (quoted && index < span.Length && span[index] == '"')
            index++;
    }

    return result;
}

/// <summary>
/// Renders truncated repository text blocks inside an AnsiConsole wrapper panel.
/// </summary>
/// <param name="diff">Raw diff text.</param>
/// <param name="maxDiffCharacters">Maximum character threshold before appending the truncated marker.</param>
static void RenderDiffOutput(string diff, int maxDiffCharacters)
{
    var output = TruncateForPrompt(diff, maxDiffCharacters);
    AnsiConsole.Write(new Panel(new Text(output))
        .Header("[deepskyblue2]Diff[/]")
        .Border(BoxBorder.Rounded)
        .Expand());
}

/// <summary>
/// Executes porcelain status to discover modified or untracked elements for interactive staging.
/// </summary>
/// <param name="repoRoot">Absolute path to repository root.</param>
/// <param name="cancellationToken">Token to cancel background status queries.</param>
/// <returns>A unique list of file paths ready for staging.</returns>
static async Task<ErrorOr<List<string>>> GetUnstagedFilesAsync(string repoRoot, CancellationToken cancellationToken)
{
    var statusResult = await RunGitCommandAsync(repoRoot, cancellationToken, "status", "--porcelain", "--untracked-files=all");
    if (statusResult.IsError)
        return statusResult.FirstError;

    if (statusResult.Value.ExitCode != 0)
        return Error.Failure(description: "Unable to read git status for interactive staging.");

    var files = new List<string>();
    foreach (var line in statusResult.Value.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
    {
        var rawLine = line.TrimEnd('\r');
        if (rawLine.Length < 4)
            continue;

        var indexStatus = rawLine[0];
        var worktreeStatus = rawLine[1];
        if (worktreeStatus == ' ' && indexStatus != '?')
            continue;

        var path = rawLine[3..].Trim();
        if (path.Contains(" -> ", StringComparison.Ordinal))
            path = path.Split(" -> ", StringSplitOptions.TrimEntries)[^1];

        if (!string.IsNullOrWhiteSpace(path))
            files.Add(path);
    }

    return files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

/// <summary>
/// Fetches recent commit logs for contextual review.
/// </summary>
/// <param name="repoRoot">Absolute path to repository root.</param>
/// <param name="argText">Raw argument text specifying log count.</param>
/// <param name="cancellationToken">Token to cancel background log queries.</param>
static async Task HandleLogCommandAsync(string repoRoot, string argText, CancellationToken cancellationToken)
{
    var parsed = SplitArguments(argText);
    var count = 10;
    if (parsed.Count >= 1 && int.TryParse(parsed[0], out var requested) && requested > 0)
        count = Math.Min(requested, 100);

    await PrintGitCommandOutputAsync(repoRoot, cancellationToken, ["log", "--oneline", "--decorate", "-n", count.ToString()]);
}

/// <summary>
/// Attaches specific file contents as context to enhance the subsequent commit message generation.
/// </summary>
/// <param name="repoRoot">Absolute path to repository root.</param>
/// <param name="argText">Raw argument text pointing to the target file.</param>
/// <param name="maxAttachmentCharacters">Threshold to truncate oversized files.</param>
/// <param name="cancellationToken">Token to cancel file I/O.</param>
/// <returns>An <see cref="AttachedContext"/> containing relative pathing and content, or null if clearing attachments.</returns>
static async Task<ErrorOr<AttachedContext?>> HandleAttachCommandAsync(string repoRoot, string argText, int maxAttachmentCharacters, CancellationToken cancellationToken)
{
    var parsed = SplitArguments(argText);
    if (parsed.Count == 0)
        return Error.Validation(description: "Usage: /attach <file-path> | /attach clear");

    if (string.Equals(parsed[0], "clear", StringComparison.OrdinalIgnoreCase))
        return (AttachedContext?)null;

    var path = parsed[0];
    var fullPath = Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(repoRoot, path));
    if (!File.Exists(fullPath))
        return Error.NotFound(description: $"Attachment file not found: {fullPath}");

    var content = await File.ReadAllTextAsync(fullPath, cancellationToken);
    var trimmed = TruncateForPrompt(content, maxAttachmentCharacters);
    return new AttachedContext(Path.GetRelativePath(repoRoot, fullPath).Replace('\\', '/'), trimmed);
}

/// <summary>
/// Truncates oversized strings and appends a visual marker so prompt payloads remain bounded.
/// </summary>
/// <param name="value">The raw, potentially unbounded string payload.</param>
/// <param name="maxCharacters">Character ceiling before truncation kicks in.</param>
/// <returns>A bounded string payload.</returns>
static string TruncateForPrompt(string value, int maxCharacters)
{
    if (maxCharacters <= 0 || value.Length <= maxCharacters)
        return value;

    var suffix = "\n\n[...truncated...]";
    return value[..Math.Max(0, maxCharacters - suffix.Length)] + suffix;
}

/// <summary>
/// Extracts branch names directly from short status output streams.
/// </summary>
/// <param name="statusOutput">Raw stdout resulting from `git status --short --branch`.</param>
/// <returns>The localized branch name, or empty if detached/unparseable.</returns>
static string ParseBranchFromStatus(string statusOutput)
{
    if (string.IsNullOrWhiteSpace(statusOutput))
        return string.Empty;

    var firstLine = statusOutput
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .FirstOrDefault();

    if (string.IsNullOrWhiteSpace(firstLine) || !firstLine.StartsWith("## ", StringComparison.Ordinal))
        return string.Empty;

    var withoutPrefix = firstLine[3..];
    var separatorIndex = withoutPrefix.IndexOf("...", StringComparison.Ordinal);
    if (separatorIndex >= 0)
        return withoutPrefix[..separatorIndex].Trim();

    var detachedIndex = withoutPrefix.IndexOf(' ');
    return detachedIndex >= 0 ? withoutPrefix[..detachedIndex].Trim() : withoutPrefix.Trim();
}

/// <summary>
/// Prints formatted REPL slash-command usage guidelines to standard out.
/// </summary>
static void PrintReplHelp()
{
    var table = new Table().RoundedBorder();
    table.AddColumn("[deepskyblue2]Command[/]");
    table.AddColumn("[deepskyblue2]Description[/]");
    void AddRow(string command, string description)
        => table.AddRow(new Markup(Markup.Escape(command)), new Markup(Markup.Escape(description)));

    AddRow("/help", "Show help");
    AddRow("/status", "Show git status --short --branch");
    AddRow("/diff [--staged|--unstaged]", "Show diff (defaults to staged if available)");
    AddRow("/add [paths]", "Stage files (interactive selector when omitted)");
    AddRow("/reset [paths]", "Unstage files (default: .)");
    AddRow("/commit", "Generate commit message and commit staged changes");
    AddRow("/amend", "Generate message and amend latest commit");
    AddRow("/push [remote] [branch]", "Push to remote (policy checks apply)");
    AddRow("/log [count]", "Show recent commit history (default: 10)");
    AddRow("/attach <file>|clear", "Attach one-shot file context for next /commit or /amend");
    AddRow("/model", "Show available models and pick one");
    AddRow("/dry-run [on|off|toggle]", "Toggle command simulation mode");
    AddRow("/cache [stats|clear]", "Show cache info or clear cache");
    AddRow("/pr", "Generate PR title/body/checklist/testing notes from current changes");
    AddRow("/review", "Generate change review: summary, risks, missing tests, rollback notes");
    AddRow("/refine <instruction>", "Refine the last generated commit message without recomputing full diff");
    AddRow("/undo", "Soft-reset HEAD~1 (confirmation required)");
    AddRow("/exit", "Exit REPL");

    AnsiConsole.Write(table);
    WriteInfo("Tip: type plain text (without /) before /commit to set a style hint.");
}

/// <summary>
/// Prompts the user synchronously for binary yes/no confirmation.
/// </summary>
/// <param name="prompt">Question prompt to present to the user.</param>
/// <returns>True if the user confirmed; otherwise false.</returns>
static bool AskForConfirmation(string prompt)
{
    AnsiConsole.Markup($"[deepskyblue2]{Markup.Escape(prompt)}[/]");
    var response = Console.ReadLine()?.Trim();
    return string.Equals(response, "y", StringComparison.OrdinalIgnoreCase)
        || string.Equals(response, "yes", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Decomposes raw REPL input into canonical command enumerations and argument trailing strings.
/// </summary>
/// <param name="input">Raw string input captured from the REPL shell.</param>
/// <returns>A validated <see cref="ParsedReplCommand"/> struct mapping strings to operations.</returns>
static ParsedReplCommand ParseReplCommand(string input)
{
    var split = input.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    if (split.Length == 0)
        return new ParsedReplCommand(ReplCommand.Unknown, string.Empty, string.Empty);

    var rawCommand = split[0];
    var argText = split.Length > 1 ? split[1] : string.Empty;

    var command = LookupTables.ReplCommandMap.TryGetValue(rawCommand, out var mappedCommand)
        ? mappedCommand
        : ReplCommand.Unknown;

    return new ParsedReplCommand(command, argText, rawCommand);
}

/// <summary>
/// Resolves /dry-run command arguments into an overarching boolean state indicator.
/// </summary>
/// <param name="argument">Trailing command arguments provided after /dry-run.</param>
/// <param name="currentValue">The existing dry-run state.</param>
/// <returns>The requested dry-run state resolved logically from the input argument.</returns>
static bool ResolveDryRunCommand(string argument, bool currentValue)
{
    var value = argument.Trim().ToLowerInvariant();
    return value switch
    {
        "" or "toggle" => !currentValue,
        "on" or "true" or "1" => true,
        "off" or "false" or "0" => false,
        _ => !currentValue,
    };
}

/// <summary>
/// Computes a stable, allocation-free SHA-256 cache key used to deduplicate iterative commit generation requests.
/// </summary>
/// <param name="status">Repository status text payload.</param>
/// <param name="diff">Diff text payload.</param>
/// <param name="branch">Current active branch.</param>
/// <param name="amend">Commit intention toggle (true for amend, false for initial commit).</param>
/// <param name="styleHint">User-supplied style instructions impacting model generation rules.</param>
/// <param name="gitConfig">Commit template settings governing output shapes.</param>
/// <returns>Hex string representation of the resultant SHA-256 stream.</returns>
static string ComputeCommitDiffKey(
    string status,
    string diff,
    string branch,
    bool amend,
    string? styleHint,
    GitToolConfig gitConfig)
{
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    AppendHashSegment(hash, status);
    AppendHashSegment(hash, diff);
    AppendHashSegment(hash, branch);
    AppendHashSegment(hash, amend ? "1" : "0");
    AppendHashSegment(hash, styleHint ?? string.Empty);
    AppendHashSegment(hash, gitConfig.UseConventionalCommits ? "1" : "0");
    AppendHashSegment(hash, gitConfig.DefaultCommitType);
    AppendHashSegment(hash, gitConfig.CustomTemplate ?? string.Empty);

    return Convert.ToHexString(hash.GetHashAndReset());
}

/// <summary>
/// Incrementally streams a string segment into a unified hash instance without relying on memory-heavy string concatenation.
/// </summary>
/// <param name="hash">The active IncrementalHash accumulator.</param>
/// <param name="value">The specific value to inject into the active byte stream.</param>
static void AppendHashSegment(IncrementalHash hash, string value)
{
    var maxBytes = Encoding.UTF8.GetMaxByteCount(value.Length + 1);
    var rented = ArrayPool<byte>.Shared.Rent(maxBytes);

    try
    {
        var byteCount = Encoding.UTF8.GetBytes(value.AsSpan(), rented.AsSpan());
        hash.AppendData(rented, 0, byteCount);

        rented[byteCount] = (byte)'\n';
        hash.AppendData(rented, byteCount, 1);
    }
    finally
    {
        ArrayPool<byte>.Shared.Return(rented);
    }
}

/// <summary>
/// Hydrates a previously persisted commit cache from disk storage.
/// </summary>
/// <param name="repoRoot">Absolute path to repository root.</param>
/// <param name="gitConfig">Repository caching policies and targeted file paths.</param>
/// <param name="cancellationToken">Token to monitor for task cancellation.</param>
/// <returns>The loaded <see cref="CommitMessageCacheStore"/>, initializing an empty one if parsing faults.</returns>
static async Task<CommitMessageCacheStore> LoadCommitMessageCacheAsync(string repoRoot, GitToolConfig gitConfig, CancellationToken cancellationToken)
{
    var cachePath = Path.IsPathRooted(gitConfig.CommitMessageCacheFile)
        ? gitConfig.CommitMessageCacheFile
        : Path.Combine(repoRoot, gitConfig.CommitMessageCacheFile);

    if (!File.Exists(cachePath))
        return new CommitMessageCacheStore(repoRoot, cachePath, new Dictionary<string, CommitMessageCacheEntry>(), false);

    try
    {
        var data = await ReadJsonFileAsync(cachePath, CommitMessageCacheFileSerializerContext.Default.CommitMessageCacheFile, cancellationToken);
        return new CommitMessageCacheStore(
            repoRoot,
            cachePath,
            data?.Entries ?? [],
            data?.GitIgnoreCheckCompleted ?? false);
    }
    catch
    {
        return new CommitMessageCacheStore(repoRoot, cachePath, new Dictionary<string, CommitMessageCacheEntry>(), false);
    }
}

/// <summary>
/// Serializes the active commit message tracking cache back to persistent storage.
/// </summary>
/// <param name="cacheStore">In-memory tracking structure indicating caching policies.</param>
/// <param name="cancellationToken">Token to monitor for cancellation.</param>
/// <returns><see cref="Result.Success"/> upon completely committing JSON to disk.</returns>
static async Task<ErrorOr<Success>> SaveCommitMessageCacheAsync(CommitMessageCacheStore cacheStore, CancellationToken cancellationToken)
{
    try
    {
        var directory = Path.GetDirectoryName(cacheStore.CacheFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var data = new CommitMessageCacheFile(cacheStore.Entries, cacheStore.GitIgnoreCheckCompleted);
        await WriteJsonFileAsync(cacheStore.CacheFilePath, data, CommitMessageCacheFileSerializerContext.Default.CommitMessageCacheFile, cancellationToken);
        return Result.Success;
    }
    catch (Exception ex)
    {
        return Error.Failure(description: ex.Message);
    }
}

/// <summary>
/// Ensures the active cache payload is properly ignored by git without duplicating exclusion entries in the local `.gitignore`.
/// </summary>
/// <param name="cacheStore">The specific cache configuration mapped to the repository root.</param>
/// <param name="cancellationToken">Token to cancel background file modifications.</param>
/// <returns><see cref="Result.Success"/> indicating `.gitignore` updates correctly flushed.</returns>
static async Task<ErrorOr<Success>> EnsureCacheFileIgnoredOnceAsync(CommitMessageCacheStore cacheStore, CancellationToken cancellationToken)
{
    var repoRoot = Path.GetFullPath(cacheStore.RepoRoot);
    var cachePath = Path.GetFullPath(cacheStore.CacheFilePath);

    if (!cachePath.StartsWith(repoRoot, StringComparison.OrdinalIgnoreCase))
        return Result.Success;

    var relativeCachePath = Path.GetRelativePath(repoRoot, cachePath)
        .Replace('\\', '/')
        .Trim();

    if (string.IsNullOrWhiteSpace(relativeCachePath))
        return Result.Success;

    var gitIgnorePath = Path.Combine(repoRoot, ".gitignore");
    var existingLines = File.Exists(gitIgnorePath)
        ? [.. await File.ReadAllLinesAsync(gitIgnorePath, cancellationToken)]
        : new List<string>();

    static string NormalizeIgnorePattern(string value) => value.Trim().Replace('\\', '/').TrimStart('/');

    var normalizedTarget = NormalizeIgnorePattern(relativeCachePath);
    var alreadyIgnored = existingLines
        .Select(NormalizeIgnorePattern)
        .Any(line => string.Equals(line, normalizedTarget, StringComparison.OrdinalIgnoreCase));

    if (alreadyIgnored)
        return Result.Success;

    if (existingLines.Count > 0 && !string.IsNullOrWhiteSpace(existingLines[^1]))
        existingLines.Add(string.Empty);

    existingLines.Add(relativeCachePath);
    await File.WriteAllLinesAsync(gitIgnorePath, existingLines, cancellationToken);
    WriteInfo($"Added '{relativeCachePath}' to .gitignore");
    return Result.Success;
}

/// <summary>
/// Routes top-level cache tracking interactions initiated via the REPL.
/// </summary>
/// <param name="cacheStore">Shared memory payload representing the repository cache file.</param>
/// <param name="argText">Raw command payload differentiating stats versus clear paths.</param>
/// <param name="cancellationToken">Global cancellation token.</param>
static async Task HandleCacheCommandAsync(CommitMessageCacheStore cacheStore, string argText, CancellationToken cancellationToken)
{
    var mode = argText.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(mode) || mode == "stats")
    {
        await PrintCacheStatsAsync(cacheStore);
        return;
    }

    if (mode == "clear")
    {
        cacheStore.Entries.Clear();
        var saveResult = await SaveCommitMessageCacheAsync(cacheStore, cancellationToken);
        if (saveResult.IsError)
        {
            WriteError($"Cache Error: {saveResult.FirstError.Description}");
            return;
        }

        WriteSuccess("Commit message cache cleared.");
        return;
    }

    WriteInfo("Usage: /cache [stats|clear]");
}

/// <summary>
/// Surfaces diagnostics related to cache file locations and total generation counts.
/// </summary>
/// <param name="cacheStore">In-memory representation of existing cache mapping blocks.</param>
/// <returns>Execution wrapper for stat propagation.</returns>
static Task PrintCacheStatsAsync(CommitMessageCacheStore cacheStore)
{
    var count = cacheStore.Entries.Count;
    WriteInfo($"Cache file: {cacheStore.CacheFilePath}");
    WriteInfo($"Entries: {count}");

    if (count > 0)
    {
        var latest = cacheStore.Entries.Values
            .Select(entry => entry.CreatedAtUtc)
            .Where(value => DateTime.TryParse(value, out _))
            .OrderByDescending(value => value)
            .FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(latest))
            WriteInfo($"Latest entry (UTC): {latest}");
    }

    return Task.CompletedTask;
}

/// <summary>
/// Scans the currently activated directory mapping to strictly enforce repository-level command execution bounds.
/// </summary>
/// <param name="cancellationToken">Global cancellation token.</param>
/// <returns>Repository path if mapping succeeds; otherwise an <see cref="Error.Validation"/> explaining boundaries.</returns>
static async Task<ErrorOr<string>> EnsureRunningAtGitRepoRootAsync(CancellationToken cancellationToken)
{
    var currentDirectory = Directory.GetCurrentDirectory();
    var gitRootResult = await RunGitCommandAsync(currentDirectory, cancellationToken, "rev-parse", "--show-toplevel");

    if (gitRootResult.IsError)
        return gitRootResult.FirstError;

    if (gitRootResult.Value.ExitCode != 0 || string.IsNullOrWhiteSpace(gitRootResult.Value.StdOut))
        return Error.Validation(description: "Current directory is not a git repository.");

    var repoRoot = Path.GetFullPath(gitRootResult.Value.StdOut.Trim());
    var current = Path.GetFullPath(currentDirectory);

    if (!string.Equals(repoRoot.TrimEnd(Path.DirectorySeparatorChar), current.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
    {
        return Error.Validation(description: $"Run this tool at repository root. Current: {current}, Root: {repoRoot}");
    }

    return repoRoot;
}

/// <summary>
/// Dispatches one-off command executions, formatting response streams sequentially.
/// </summary>
/// <param name="repoRoot">Location mapping executing commands to repository paths.</param>
/// <param name="cancellationToken">Token to cancel background Git operations.</param>
/// <param name="args">String array payload representing raw Git command parameters.</param>
static async Task PrintGitCommandOutputAsync(string repoRoot, CancellationToken cancellationToken, IEnumerable<string> args)
{
    var result = await RunGitCommandAsync(repoRoot, cancellationToken, [.. args]);

    if (result.IsError)
    {
        WriteError(result.FirstError.Description);
        return;
    }

    if (result.Value.ExitCode != 0)
    {
        WriteError(result.Value.StdErr.Trim());
        return;
    }

    if (string.IsNullOrWhiteSpace(result.Value.StdOut))
        WriteInfo("(no output)");
    else
        AnsiConsole.WriteLine(result.Value.StdOut.TrimEnd());
}

/// <summary>
/// Interacts directly with the OS environment using <see cref="ProcessStartInfo"/> wrapper payloads, trapping expected termination scenarios.
/// </summary>
/// <param name="workingDirectory">Base folder setting the git invocation boundary limits.</param>
/// <param name="cancellationToken">Token intercepting OS signals preventing zombie process leaks.</param>
/// <param name="args">Execution string chunks.</param>
/// <returns>Standard out and error payloads wrapped within <see cref="GitCommandResult"/>.</returns>
static async Task<ErrorOr<GitCommandResult>> RunGitCommandAsync(string workingDirectory, CancellationToken cancellationToken, params string[] args)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = "git",
        WorkingDirectory = workingDirectory,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };

    foreach (var arg in args)
        startInfo.ArgumentList.Add(arg);

    using var process = new Process
    {
        StartInfo = startInfo,
    };

    try
    {
        process.Start();
    }
    catch (Win32Exception ex)
    {
        return Error.Failure(description: $"Unable to start git process. Ensure git is installed and on PATH. {ex.Message}");
    }
    catch (Exception ex)
    {
        return Error.Failure(description: $"Failed to start git process. {ex.Message}");
    }

    try
    {
        using var cancelRegistration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
        });

        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync(cancellationToken);
        var stdOut = await stdOutTask;
        var stdErr = await stdErrTask;

        return new GitCommandResult(process.ExitCode, stdOut, stdErr);
    }
    catch (OperationCanceledException)
    {
        return Error.Failure(description: "Git command canceled by user.");
    }
}

/// <summary>
/// Intercepts Copilot configuration requirements bridging local model capabilities to the GitHub SDK architecture wrapper.
/// </summary>
/// <param name="profileName">Profile configuration key.</param>
/// <param name="profile">Target profile defining OpenAI bypasses or Copilot GitHub routing.</param>
/// <param name="globalMcpServers">Root context MCP capabilities passed to downstream session builder parameters.</param>
/// <param name="showRoutingBanner">Determines visual presentation of banner components indicating initialization state transitions.</param>
/// <param name="cancellationToken">Global cancellation token.</param>
/// <returns>An instantiated SDK execution wrapper containing context maps supporting BYOK overrides.</returns>
static async Task<ErrorOr<SessionConfig>> BuildSessionConfigAsync(
    string profileName,
    ProviderProfile profile,
    IReadOnlyDictionary<string, McpServerConfig>? globalMcpServers,
    bool showRoutingBanner,
    CancellationToken cancellationToken)
{
    var providerTypeResult = ResolveProviderType(profile.ProviderType);
    if (providerTypeResult.IsError)
        return providerTypeResult.FirstError;

    var sessionConfig = new SessionConfig
    {
        OnPermissionRequest = PermissionHandler.ApproveAll,
        Streaming = true,
    };

    var mergedMcpServers = BuildMergedMcpServers(globalMcpServers, profile.McpServers);
    if (mergedMcpServers.Count > 0)
        sessionConfig.McpServers = mergedMcpServers;

    var normalizedProviderType = providerTypeResult.Value;
    if (normalizedProviderType == "openai")
    {
        var modelId = profile.Model;
        if (string.IsNullOrWhiteSpace(modelId))
            modelId = await FetchActiveModelAsync(profile.BaseUrl!, profile.ApiKey, ResolveModelDiscoveryTimeout(profile), cancellationToken);

        if (modelId is null)
        {
            return Error.Validation(description:
                "No model configured or discoverable from local provider. Set 'Model' in profile or ensure /models returns at least one model.");
        }

        if (showRoutingBanner)
            WriteInfo($"[Routing to Local: {profileName} | Model: {modelId}]");

        sessionConfig.Model = modelId;
        sessionConfig.Provider = new ProviderConfig
        {
            Type = "openai",
            BaseUrl = profile.BaseUrl!,
            ApiKey = profile.ApiKey ?? "local"
        };
    }
    else if (showRoutingBanner)
    {
        WriteInfo("[Routing to Cloud: GitHub Copilot]");
    }

    return sessionConfig;
}

/// <summary>
/// Merges disparate configuration sets dictating local tool capabilities via the Model Context Protocol (MCP).
/// </summary>
/// <param name="globalMcpServers">Tools loaded universally regardless of model routing blocks.</param>
/// <param name="profileMcpServers">Tools uniquely available to specific profile paths.</param>
/// <returns>A unified mapping object digestible by the GitHub Copilot SDK environment builder payload.</returns>
static Dictionary<string, object> BuildMergedMcpServers(
    IReadOnlyDictionary<string, McpServerConfig>? globalMcpServers,
    IReadOnlyDictionary<string, McpServerConfig>? profileMcpServers)
{
    var merged = new Dictionary<string, McpServerConfig>(StringComparer.OrdinalIgnoreCase);

    if (globalMcpServers is not null)
    {
        foreach (var (name, server) in globalMcpServers)
            merged[name] = server;
    }

    if (profileMcpServers is not null)
    {
        foreach (var (name, server) in profileMcpServers)
            merged[name] = server;
    }

    var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
    foreach (var (name, server) in merged)
    {
        var serverConfig = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(server.Url))
            serverConfig["url"] = server.Url;
        if (!string.IsNullOrWhiteSpace(server.Command))
            serverConfig["command"] = server.Command;
        if (server.Args is { Count: > 0 })
            serverConfig["args"] = server.Args;
        if (server.Env is { Count: > 0 })
            serverConfig["env"] = server.Env;

        if (serverConfig.Count > 0)
            result[name] = serverConfig;
    }

    return result;
}

/// <summary>
/// Normalizes provider type aliases (e.g., 'Cloud' to 'github') to canonical SDK provider identifiers.
/// </summary>
/// <param name="providerType">User-defined configuration alias identifying routing mechanism.</param>
/// <returns>A normalized string representing standard SDK capability blocks.</returns>
static ErrorOr<string> ResolveProviderType(string? providerType)
{
    var normalized = providerType?.Trim();
    if (string.IsNullOrWhiteSpace(normalized))
        return Error.Validation(description: $"Unsupported ProviderType '{providerType}'. Supported values: GitHub, OpenAICompatible.");

    if (LookupTables.ProviderAliasMap.TryGetValue(normalized, out var mappedProvider))
        return mappedProvider;

    return Error.Validation(description: $"Unsupported ProviderType '{providerType}'. Supported values: GitHub, OpenAICompatible.");
}

/// <summary>
/// Queries standard OpenAI-compatible `/models` endpoints capturing the first registered identification string for automated model selection flows.
/// </summary>
/// <param name="baseUrl">Target server URL (e.g., LM Studio REST API endpoint).</param>
/// <param name="apiKey">Optional authorization identifier mapped to request boundaries.</param>
/// <param name="timeout">Limits boundary connection hanging requests.</param>
/// <param name="cancellationToken">Global cancellation token.</param>
/// <returns>A string modeling identifier matching specific local engine payloads.</returns>
static async Task<string?> FetchActiveModelAsync(string baseUrl, string? apiKey, TimeSpan? timeout, CancellationToken cancellationToken)
{
    using var timeoutCts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(15));
    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);

    var modelsEndpoint = $"{baseUrl.TrimEnd('/')}/models";
    using var request = new HttpRequestMessage(HttpMethod.Get, modelsEndpoint);
    
    if (!string.IsNullOrEmpty(apiKey))
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

    try
    {
        var response = await SharedResources.Http.SendAsync(request, linkedCts.Token);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(linkedCts.Token);
        var modelData = JsonSerializer.Deserialize(json, ModelResponseSerializerContext.Default.ModelResponse);
        return modelData?.Data.FirstOrDefault()?.Id;
    }
    catch (HttpRequestException ex)
    {
        WriteWarning($"Warning: Failed to fetch models from {modelsEndpoint}. HTTP error: {ex.Message}");
        return null;
    }
    catch (OperationCanceledException)
    {
        WriteWarning($"Warning: Model fetching canceled or timed out.");
        return null;
    }
    catch (Exception ex)
    {
        WriteWarning($"Warning: Failed to fetch models from {modelsEndpoint}. {ex.Message}");
        return null;
    }
}

/// <summary>
/// Discovers model inventory lists from localized inference engines via API boundaries matching exact `/models` patterns.
/// </summary>
/// <param name="baseUrl">Root REST endpoint matching provider routing boundaries.</param>
/// <param name="apiKey">Optional token mapped to Authorization headers.</param>
/// <param name="timeout">Prevents interface hanging from unreachable HTTP servers.</param>
/// <param name="cancellationToken">Global token to cancel background HTTP interaction.</param>
/// <returns>A complete string collection of identifiers available to local rendering processes.</returns>
static async Task<ErrorOr<List<string>>> FetchAvailableModelsAsync(string baseUrl, string? apiKey, TimeSpan? timeout, CancellationToken cancellationToken)
{
    using var timeoutCts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(15));
    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);

    var modelsEndpoint = $"{baseUrl.TrimEnd('/')}/models";
    using var request = new HttpRequestMessage(HttpMethod.Get, modelsEndpoint);
    
    if (!string.IsNullOrEmpty(apiKey))
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

    try
    {
        var response = await SharedResources.Http.SendAsync(request, linkedCts.Token);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(linkedCts.Token);
        var modelData = JsonSerializer.Deserialize(json, ModelResponseSerializerContext.Default.ModelResponse);
        var models = modelData?.Data?
            .Select(model => model.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToList() ?? new List<string>();

        return models;
    }
    catch (OperationCanceledException)
    {
        return Error.Failure(description: timeoutCts.IsCancellationRequested ? "Model request timed out." : "Request canceled.");
    }
    catch (Exception ex)
    {
        return Error.Failure(description: $"Failed to load models from {modelsEndpoint}. {ex.Message}");
    }
}

/// <summary>
/// Implements standard CLI wrapper functionality pushing static string variables through the execution pipeline into Copilot model engine bounds.
/// </summary>
/// <param name="profileName">Profile configuration key.</param>
/// <param name="profile">Target profile defining API boundaries.</param>
/// <param name="prompt">User defined string mapping.</param>
/// <param name="verbose">Toggles detailed context printing limits.</param>
/// <param name="globalMcpServers">Server targets mapped into tool interaction block capabilities.</param>
/// <param name="cancellationToken">Global cancellation token.</param>
static async Task ExecuteAgentWorkflowAsync(
    string profileName,
    ProviderProfile profile,
    string prompt,
    bool verbose,
    IReadOnlyDictionary<string, McpServerConfig>? globalMcpServers,
    CancellationToken cancellationToken)
{
    var sessionConfigResult = await BuildSessionConfigAsync(profileName, profile, globalMcpServers, showRoutingBanner: true, cancellationToken);
    if (sessionConfigResult.IsError)
    {
        WriteError($"Configuration Error: {sessionConfigResult.FirstError.Description}");
        return;
    }

    var sessionConfig = sessionConfigResult.Value;

    await using var client = new CopilotClient();
    try
    {
        await client.StartAsync(cancellationToken);
    }
    catch (Exception ex)
    {
        WriteError($"Failed to start Copilot client: {ex.Message}");
        return;
    }

    try
    {
        await using var session = await client.CreateSessionAsync(sessionConfig, cancellationToken);
        var printedStreamChunk = false;
        var printedReasoningPrefix = false;

        using var subscription = session.On(evt =>
        {
            var eventName = evt.GetType().Name;

            if (evt is AssistantMessageDeltaEvent deltaEvent)
            {
                var chunk = deltaEvent.Data?.DeltaContent;
                if (!string.IsNullOrEmpty(chunk))
                {
                    AnsiConsole.Write(new Text(chunk));
                    printedStreamChunk = true;
                }

                return;
            }

            if (verbose && evt is AssistantReasoningDeltaEvent reasoningDeltaEvent)
            {
                var reasoningChunk = reasoningDeltaEvent.Data?.DeltaContent;
                if (!string.IsNullOrEmpty(reasoningChunk))
                {
                    if (!printedReasoningPrefix)
                    {
                        AnsiConsole.Markup("\n[darkcyan][Reasoning] [/]");
                        printedReasoningPrefix = true;
                    }

                    AnsiConsole.Markup($"[darkcyan]{Markup.Escape(reasoningChunk)}[/]");
                }

                return;
            }

            if (verbose && IsToolStartEvent(eventName))
            {
                WriteWarning($"[Tool event: {eventName}]");
                return;
            }

            if (verbose && !IsSessionIdleEvent(eventName))
            {
                WriteInfo($"[Event: {eventName}]");
            }
        });

        if (verbose) WriteInfo("[Session created | Streaming enabled]");

        var requestTimeout = ResolveRequestTimeout(profile);

        var response = await session.SendAndWaitAsync(new MessageOptions
        {
            Prompt = prompt,
        }, timeout: requestTimeout);

        if (!printedStreamChunk && !string.IsNullOrWhiteSpace(response?.Data?.Content))
        {
            AnsiConsole.Write(new Text(response.Data.Content));
        }
        else if (!printedStreamChunk)
        {
            WriteWarning("[No response content returned]");
        }

        if (printedReasoningPrefix) AnsiConsole.WriteLine();
    }
    catch (OperationCanceledException)
    {
        WriteInfo("\nOperation canceled or timed out.");
    }
    catch (Exception ex)
    {
        WriteError($"Run failed: {ex.Message}");
        return;
    }

    AnsiConsole.WriteLine();
}

/// <summary>
/// Determines whether a session event looks like a tool start/progress signal.
/// </summary>
/// <param name="eventName">Event string matching capability blocks.</param>
/// <returns>True if tool execution strings match pattern constraints.</returns>
static bool IsToolStartEvent(string eventName)
{
    return eventName.Contains("tool", StringComparison.OrdinalIgnoreCase)
        && (eventName.Contains("start", StringComparison.OrdinalIgnoreCase)
            || eventName.Contains("execution", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Determines whether a session event signals model idle/turn completion.
/// </summary>
/// <param name="eventName">Event string identifying idle process tracking elements.</param>
/// <returns>True if idle indicators fire inside Copilot event pipeline streams.</returns>
static bool IsSessionIdleEvent(string eventName)
{
    return eventName.Contains("idle", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Determines explicit fallback HTTP request timeout windows resolving configurations based on cloud vs. local routing endpoints.
/// </summary>
/// <param name="profile">Profile defining inference interaction timeouts.</param>
/// <returns>TimeSpan ceiling matching profile bounds.</returns>
static TimeSpan ResolveRequestTimeout(ProviderProfile profile)
{
    if (profile.RequestTimeoutSeconds is > 0)
        return TimeSpan.FromSeconds(profile.RequestTimeoutSeconds.Value);

    var providerType = ResolveProviderType(profile.ProviderType);
    return providerType.IsError
        ? TimeSpan.FromSeconds(60)
        : providerType.Value == "openai"
            ? TimeSpan.FromSeconds(300)
            : TimeSpan.FromSeconds(60);
}

/// <summary>
/// Determines explicit HTTP discovery timeout bounds parsing `/models` structures.
/// </summary>
/// <param name="profile">Profile configuration defining model fetching threshold bounds.</param>
/// <returns>TimeSpan dictating maximum connection delay values.</returns>
static TimeSpan ResolveModelDiscoveryTimeout(ProviderProfile profile)
{
    if (profile.ModelDiscoveryTimeoutSeconds is > 0)
        return TimeSpan.FromSeconds(profile.ModelDiscoveryTimeoutSeconds.Value);

    var providerType = ResolveProviderType(profile.ProviderType);
    return providerType.IsError
        ? TimeSpan.FromSeconds(15)
        : providerType.Value == "openai"
            ? TimeSpan.FromSeconds(30)
            : TimeSpan.FromSeconds(15);
}

/// <summary>
/// Identifies expected REPL shell operations triggered via prefix slash configurations.
/// </summary>
public enum ReplCommand
{
    /// <summary>Unmapped string argument parsing fallback.</summary>
    Unknown,
    /// <summary>Indicates standard operation printing guide boundaries.</summary>
    Help,
    /// <summary>Indicates termination flow processes.</summary>
    Exit,
    /// <summary>Maps `git status` string wrappers.</summary>
    Status,
    /// <summary>Maps `git diff` capabilities encapsulating file changes.</summary>
    Diff,
    /// <summary>Maps `git add` interactive selection bounds.</summary>
    Add,
    /// <summary>Maps `git restore` removal mechanisms un-staging specific index targets.</summary>
    Reset,
    /// <summary>Initiates git execution generating message schemas.</summary>
    Commit,
    /// <summary>Initiates git amend generating trailing tracking schemas overriding root contexts.</summary>
    Amend,
    /// <summary>Maps remote synchronization wrappers bypassing validation checks unless blocked.</summary>
    Push,
    /// <summary>Indicates boolean simulation state overriding process modifications.</summary>
    DryRun,
    /// <summary>Surfaces repository-specific json metadata file diagnostics tracking cache structures.</summary>
    Cache,
    /// <summary>Intercepts `/models` API to enforce local profile execution blocks interactively.</summary>
    Model,
    /// <summary>Intercepts soft resets pulling repository heads backward one count safely.</summary>
    Undo,
    /// <summary>Parses log command formatting outputs displaying commit trees efficiently.</summary>
    Log,
    /// <summary>Interacts with system I/O bounds extracting explicit file contents rendering prompt blocks temporarily.</summary>
    Attach,
    /// <summary>Generates pull request metadata from current repository context.</summary>
    Pr,
    /// <summary>Generates repository review notes for current changes.</summary>
    Review,
    /// <summary>Refines the latest generated commit message using conversational context.</summary>
    Refine,
}

/// <summary>
/// Regulates continuation execution state bounds governing asynchronous while-loops evaluating REPL strings.
/// </summary>
public enum ReplLoopControl
{
    /// <summary>Command handled, looping evaluates following line.</summary>
    Continue,
    /// <summary>Command terminates bounds escaping running environments.</summary>
    Exit,
}

/// <summary>
/// Contains a structured, parsed REPL command including the typed intent and any additional payload text.
/// </summary>
/// <param name="Command">The mapped <see cref="ReplCommand"/> derived from the raw input string.</param>
/// <param name="ArgText">The substring of arguments appearing after the command identifier.</param>
/// <param name="RawCommand">The specific lowercase command prefix typed by the user.</param>
public sealed record ParsedReplCommand(ReplCommand Command, string ArgText, string RawCommand);

/// <summary>
/// Wraps targeted file contents read into memory and mapped to downstream prompt variables.
/// </summary>
/// <param name="Path">The relative repository path identifying the attached context origin.</param>
/// <param name="Content">The bounded string payload parsed out of the file.</param>
public sealed record AttachedContext(string Path, string Content);

/// <summary>
/// Handles graceful shutdown signals hooking into `Console.CancelKeyPress` to terminate system threads avoiding zombie resource leaks.
/// </summary>
public static class ConsoleCancelCoordinator
{
    private static readonly CancellationTokenSource Source = new();
    private static int initialized;

    /// <summary>
    /// The globally accessible <see cref="CancellationToken"/> triggered when a user signals an interrupt (e.g. Ctrl+C).
    /// </summary>
    public static CancellationToken Token => Source.Token;

    /// <summary>
    /// Initializes global interrupt handlers. Subsequent calls to this method perform no operation.
    /// </summary>
    public static void Initialize()
    {
        if (Interlocked.Exchange(ref initialized, 1) == 1)
            return;

        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            if (!Source.IsCancellationRequested)
                Source.Cancel();
        };
    }
}

/// <summary>
/// Provides shared HTTP resources for the process lifetime to prevent avoidable socket churn.
/// </summary>
public static class SharedResources
{
    private static readonly SocketsHttpHandler HttpHandler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        MaxConnectionsPerServer = 16,
        AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
    };

    public static readonly HttpClient Http = new(HttpHandler, disposeHandler: false)
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };
}

/// <summary>
/// Precomputed lookup maps for hot-path command/provider parsing.
/// </summary>
public static class LookupTables
{
    public static readonly FrozenDictionary<string, ReplCommand> ReplCommandMap =
        new Dictionary<string, ReplCommand>(StringComparer.OrdinalIgnoreCase)
        {
            ["/help"] = ReplCommand.Help,
            ["/exit"] = ReplCommand.Exit,
            ["/quit"] = ReplCommand.Exit,
            ["/status"] = ReplCommand.Status,
            ["/diff"] = ReplCommand.Diff,
            ["/add"] = ReplCommand.Add,
            ["/reset"] = ReplCommand.Reset,
            ["/commit"] = ReplCommand.Commit,
            ["/amend"] = ReplCommand.Amend,
            ["/push"] = ReplCommand.Push,
            ["/dry-run"] = ReplCommand.DryRun,
            ["/cache"] = ReplCommand.Cache,
            ["/model"] = ReplCommand.Model,
            ["/undo"] = ReplCommand.Undo,
            ["/log"] = ReplCommand.Log,
            ["/attach"] = ReplCommand.Attach,
            ["/pr"] = ReplCommand.Pr,
            ["/review"] = ReplCommand.Review,
            ["/refine"] = ReplCommand.Refine,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static readonly FrozenDictionary<string, string> ProviderAliasMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["github"] = "github",
            ["cloud"] = "github",
            ["openaicompatible"] = "openai",
            ["openai"] = "openai",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Immutable runtime projection of configuration lookups for high-frequency reads.
/// </summary>
public sealed class RuntimeConfigView
{
    public RuntimeConfigView(
        FrozenDictionary<string, ProviderProfile> profiles,
        string defaultProfile,
        GitToolConfig git,
        ReplConfig repl,
        FrozenDictionary<string, McpServerConfig> globalMcpServers)
    {
        Profiles = profiles;
        DefaultProfile = defaultProfile;
        Git = git;
        Repl = repl;
        GlobalMcpServers = globalMcpServers;
    }

    public FrozenDictionary<string, ProviderProfile> Profiles { get; }
    public string DefaultProfile { get; }
    public GitToolConfig Git { get; }
    public ReplConfig Repl { get; }
    public FrozenDictionary<string, McpServerConfig> GlobalMcpServers { get; }
}

/// <summary>
/// Root configuration encompassing provider profiles, git tool constraints, and overarching REPL schema boundaries.
/// </summary>
/// <param name="Profiles">Routing targets configuring interaction constraints matching GitHub paths or local proxy hosts.</param>
/// <param name="DefaultProfile">Indicates fallbacks matching initialized variables rendering the root profile payload string boundary.</param>
/// <param name="Git">Configures execution overrides mapping to project specific standards encompassing formatting overrides.</param>
/// <param name="Repl">Dictates console-level formatting capabilities impacting truncation blocks bounding process arrays.</param>
/// <param name="McpServers">Root tool context capabilities parsing system command payloads into language bounds accessible systemically.</param>
public sealed record AppConfig(
    [property: JsonPropertyName("Profiles")] Dictionary<string, ProviderProfile> Profiles,
    [property: JsonPropertyName("DefaultProfile")] string DefaultProfile,
    [property: JsonPropertyName("Git")] GitToolConfig? Git,
    [property: JsonPropertyName("Repl")] ReplConfig? Repl,
    [property: JsonPropertyName("McpServers")] Dictionary<string, McpServerConfig>? McpServers
);

/// <summary>
/// Provider profile configuration for cloud or OpenAI-compatible inference execution limits.
/// </summary>
/// <param name="ProviderType">Defines execution mappings resolving Copilot boundaries.</param>
/// <param name="BaseUrl">Identifies remote endpoints supporting LLM bounds.</param>
/// <param name="ApiKey">Identifies authorization bounds matching required tokens parsing headers safely.</param>
/// <param name="Model">Locates explicitly requested generation model engines overriding discovery checks.</param>
/// <param name="RequestTimeoutSeconds">Modifies boundary execution waits resolving generation loops efficiently.</param>
/// <param name="ModelDiscoveryTimeoutSeconds">Truncates blocking behaviors parsing model API discovery structures.</param>
/// <param name="McpServers">Isolated server dependencies identifying localized bounds accessible via prompt tools.</param>
public sealed record ProviderProfile(
    string ProviderType,
    string? BaseUrl,
    string? ApiKey,
    string? Model,
    int? RequestTimeoutSeconds,
    int? ModelDiscoveryTimeoutSeconds,
    Dictionary<string, McpServerConfig>? McpServers);

/// <summary>
/// Defines individual execution payloads hooking standard output and custom environment strings into MCP processes.
/// </summary>
public sealed class McpServerConfig
{
    /// <summary>Web socket connection point representing external tool capabilities.</summary>
    [JsonPropertyName("Url")]
    public string? Url { get; init; }

    /// <summary>Command executing system executable processes bridging external logic paths.</summary>
    [JsonPropertyName("Command")]
    public string? Command { get; init; }

    /// <summary>Array of argument constraints defining execution overrides supporting executable processes.</summary>
    [JsonPropertyName("Args")]
    public List<string>? Args { get; init; }

    /// <summary>Dictionary mapping environment variables passed explicitly matching process context blocks.</summary>
    [JsonPropertyName("Env")]
    public Dictionary<string, string>? Env { get; init; }
}

/// <summary>
/// Wraps standardized OpenAPI responses querying specific system capabilities parsing multiple <see cref="ModelInfo"/> payloads.
/// </summary>
/// <param name="Data">Array of model strings identifying available system context endpoints mapping directly to routing processes.</param>
public sealed record ModelResponse([property: JsonPropertyName("data")] List<ModelInfo> Data);

/// <summary>
/// Identifies unique localized rendering engine payloads mapped directly to the active system context bounds.
/// </summary>
/// <param name="Id">Explicit name executing model routes determining engine capacities identifying string outputs.</param>
public sealed record ModelInfo([property: JsonPropertyName("id")] string Id);

/// <summary>
/// Parses foundational options mapping root behavior capacities identifying explicitly defined initialization flows resolving execution loops.
/// </summary>
/// <param name="ProfileName">Overrides defaults pointing matching system routing processes dynamically mapping target connections.</param>
/// <param name="Prompt">Identifies explicitly written target variables modifying root outputs explicitly parsed executing bounds.</param>
/// <param name="Verbose">Determines logging verbosity resolving debug variables formatting rendering context payloads.</param>
/// <param name="ShowHelp">Explicitly halts execution printing boundary mapping behaviors indicating string arrays clearly defining variables.</param>
/// <param name="ReplMode">Overrides string parameters looping executions processing shell logic flows isolating mapping dependencies interactively.</param>
public sealed record CliOptions(string? ProfileName, string Prompt, bool Verbose, bool ShowHelp, bool ReplMode);

/// <summary>
/// Wraps the results of an executing standard OS process block encapsulating output formatting and stream capacities tracking status limits safely.
/// </summary>
/// <param name="ExitCode">Zero mapping successful operations indicating boundary constraints passed rendering safely parsed system states.</param>
/// <param name="StdOut">Contains system stream payloads identifying executing string parameters isolated from error paths.</param>
/// <param name="StdErr">Contains system stream payloads indicating warnings modifying error blocks tracking execution arrays explicitly mapping failures.</param>
public sealed record GitCommandResult(int ExitCode, string StdOut, string StdErr);

/// <summary>
/// Maps explicit history payloads identifying cache boundaries tracking generation capacity arrays isolating execution variables dynamically.
/// </summary>
/// <param name="Message">The specific payload text generated mapping to explicitly verified cache states preventing duplication overheads isolating variables.</param>
/// <param name="CreatedAtUtc">Identifies specific timestamp bounding context capabilities.</param>
/// <param name="Branch">Identifies context paths defining explicit state branches rendering repository loops mapping dynamically mapped execution tracking mechanisms.</param>
/// <param name="Amend">Identifies context variables processing explicitly parsed capacity states matching trailing overrides tracking process bounds matching output string schemas.</param>
public sealed record CommitMessageCacheEntry(string Message, string CreatedAtUtc, string Branch, bool Amend);

/// <summary>
/// Contains the hydrated mappings of previously executed prompts bounding dictionary payloads identifying explicit cache state boundaries securely modifying capacity arrays isolating bounds identifying repository outputs.
/// </summary>
public sealed class CommitMessageCacheStore
{
    /// <summary>
    /// Instantiates memory mapping caching blocks initializing capacity variables tracking states explicitly defining output context bounding repository strings securely modifying outputs identifying paths defining dynamically executing processes isolating bounds.
    /// </summary>
    /// <param name="repoRoot">Explicit system file mapping tracking capacities mapping target root states identifying strings determining paths resolving execution tracking dynamically.</param>
    /// <param name="cacheFilePath">Explicit payload indicating file bounding mechanisms parsing local strings rendering boundary outputs mapping capacities resolving execution variables.</param>
    /// <param name="entries">Tracks history strings dynamically isolating dictionary bounds tracking generation arrays identifying explicitly mapping boundaries.</param>
    /// <param name="gitIgnoreCheckCompleted">Identifies whether explicit boundaries verified outputs mapping tracking strings verifying git limits matching output variables isolating bounding parameters preventing outputs identifying execution arrays dynamically tracking process bounds.</param>
    public CommitMessageCacheStore(string repoRoot, string cacheFilePath, Dictionary<string, CommitMessageCacheEntry> entries, bool gitIgnoreCheckCompleted)
    {
        RepoRoot = repoRoot;
        CacheFilePath = cacheFilePath;
        Entries = entries;
        GitIgnoreCheckCompleted = gitIgnoreCheckCompleted;
    }

    /// <summary>Identifies repository root payload capacities determining state string mappings identifying explicit paths executing processes dynamically.</summary>
    public string RepoRoot { get; }
    
    /// <summary>Identifies repository capacity arrays tracking string mappings executing specific path outputs determining boundaries bounding cache paths defining bounds.</summary>
    public string CacheFilePath { get; }
    
    /// <summary>Maps string tracking bounding array capacities parsing tracking states generating payload boundaries executing processes generating tracking string states mapping target variables uniquely tracking processes isolating parameters bounds.</summary>
    public Dictionary<string, CommitMessageCacheEntry> Entries { get; }
    
    /// <summary>Verifies tracking states establishing bounds matching git output tracking isolating strings generating paths explicitly tracking paths executing process limits.</summary>
    public bool GitIgnoreCheckCompleted { get; set; }
}

/// <summary>
/// Defines serializable payloads explicitly identifying dictionary mapping bounds modifying strings parsing json schemas generating mapping targets safely isolating executing boundaries dynamically parsing strings isolating process limits securely mapping outputs defining paths executing dynamically matching outputs defining bounds identifying variables mapping outputs defining capacity parameters generating targets executing specific parameter schemas defining boundary capacity strings securely rendering variables parsing states securely determining outputs executing tracking schemas matching string paths identifying boundaries defining context limits securely.
/// </summary>
public sealed class CommitMessageCacheFile
{
    /// <summary>Maps string outputs identifying JSON block generation paths isolating execution mapping states uniquely bounding array strings safely defining variables.</summary>
    [JsonPropertyName("Entries")]
    public Dictionary<string, CommitMessageCacheEntry> Entries { get; init; } = new();

    /// <summary>Determines boundary capabilities executing mapping bounds matching local file parsing limiting git repository tracking schemas capturing process configurations defining output constraints matching variables isolating capacities executing target tracking arrays explicitly determining variables securely.</summary>
    [JsonPropertyName("GitIgnoreCheckCompleted")]
    public bool GitIgnoreCheckCompleted { get; init; }

    /// <summary>Parameterless default constructor supporting JSON serialization frameworks mapping object execution target capacities.</summary>
    public CommitMessageCacheFile() { }
    
    /// <summary>Constructor supporting explicit object initialization defining serialization mapping context targets matching JSON array strings identifying bounded parameters rendering payload boundaries securely defining tracking strings isolating outputs.</summary>
    /// <param name="entries">Maps string tracking execution array boundaries targeting tracking boundaries determining specific mapping target variables tracking outputs.</param>
    /// <param name="gitIgnoreCheckCompleted">Tracks boolean state execution boundaries parsing target file outputs defining configuration logic loops generating boundaries rendering limits determining local repository paths.</param>
    public CommitMessageCacheFile(Dictionary<string, CommitMessageCacheEntry> entries, bool gitIgnoreCheckCompleted)
    {
        Entries = entries;
        GitIgnoreCheckCompleted = gitIgnoreCheckCompleted;
    }
}

/// <summary>
/// Configurations determining explicit policy arrays bounding tool paths dynamically routing formatting limits handling interaction behaviors modifying repository states explicitly executing specific variables rendering capacity arrays generating constraints defining bounds.
/// </summary>
public sealed class GitToolConfig
{
    /// <summary>Flags indicating whether explicit process overrides mapping generation bounding tracking output schemas securely defining process variables determining outputs tracking execution processes automatically deploying arrays generating target strings securely determining process parameters isolating bounds generating limits modifying string arrays mapping tracking target capacity bounds isolating targets defining bounding limits identifying outputs.</summary>
    [JsonPropertyName("AutoPush")]
    public bool AutoPush { get; init; } = false;

    /// <summary>Determines output execution variables enforcing synchronous validation processes intercepting boundaries dynamically preventing execution array parameters dynamically rendering process target logic bounds isolating limits rendering strings executing variables defining processes determining limits tracking loops identifying process targets mapping targets isolating bounds matching outputs explicitly defining loops.</summary>
    [JsonPropertyName("RequireConfirmations")]
    public bool RequireConfirmations { get; init; } = true;

    /// <summary>Identifies repository bounds restricting payload outputs modifying tracking execution array variables safely defining paths isolating process capabilities securely defining bounds tracking limits executing explicitly parsing specific configuration outputs rendering boundaries matching string variables executing path variables matching tracking strings defining bounds.</summary>
    [JsonPropertyName("ProtectedBranches")]
    public List<string> ProtectedBranches { get; init; } = new() { "main", "master" };

    /// <summary>Flags process mapping determining variables mapping output boundaries determining tracking generation logic rendering capacity loops defining mapping bounds executing output logic tracking variables executing explicitly defining array parameters explicitly tracking target strings isolating bounds parsing parameters.</summary>
    [JsonPropertyName("UseConventionalCommits")]
    public bool UseConventionalCommits { get; init; } = true;

    /// <summary>Determines specific type strings bounding tracking limits replacing default outputs executing specific capacities generating explicitly matching tracking arrays determining output tracking bounds parsing specific variables rendering strings matching arrays determining paths isolating boundary configurations executing explicit strings tracking paths determining bounds generating outputs isolating boundary variables.</summary>
    [JsonPropertyName("DefaultCommitType")]
    public string DefaultCommitType { get; init; } = "chore";

    /// <summary>Tracks explicitly bounded generation templates resolving tracking mappings generating array parameters replacing output strings safely determining execution array paths isolating limits generating tracking processes explicitly mapping tracking parameter blocks.</summary>
    [JsonPropertyName("CustomTemplate")]
    public string? CustomTemplate { get; init; }

    /// <summary>Flags executing bounds tracking strings caching payloads identifying parameters mapping limits determining generating parameters rendering boundaries determining paths matching parameter constraints tracking generation blocks matching variable isolating mapping constraints securely defining limits.</summary>
    [JsonPropertyName("EnableCommitMessageCache")]
    public bool EnableCommitMessageCache { get; init; } = true;

    /// <summary>Tracks file name identifiers modifying tracking capacity strings matching local paths identifying explicit mapping parameters determining boundaries safely bounding output arrays identifying JSON blocks capturing outputs isolating tracking strings executing variables defining execution.</summary>
    [JsonPropertyName("CommitMessageCacheFile")]
    public string CommitMessageCacheFile { get; init; } = ".gitcopilot-cache.json";

    /// <summary>Flags default executing loops parsing commands preventing mapping boundaries from mutating array strings determining processes generating variables defining capacity strings safely routing process strings isolating target mappings matching local string tracking mechanisms.</summary>
    [JsonPropertyName("DryRunDefault")]
    public bool DryRunDefault { get; init; } = false;
}

/// <summary>
/// Dictates formatting behavior capabilities generating strings isolating tracking parameters limiting explicit paths bounding console loops isolating tracking states executing payload lengths rendering variables securely defining output paths executing processes determining bounds safely mapping configurations isolating parameters generating arrays capturing explicit bounding configuration logic loops.
/// </summary>
public sealed class ReplConfig
{
    /// <summary>Defines string targets parsing console boundaries isolating parameter strings defining process execution outputs tracking limits matching user interaction strings defining variables determining output generation strings explicitly parsing specific capacities determining local terminal target arrays safely defining execution bounds identifying limits mapping paths rendering paths mapping tracking variables generating tracking targets determining strings securely.</summary>
    [JsonPropertyName("Prompt")]
    public string Prompt { get; init; } = "gitcopilot> ";

    /// <summary>Bounds explicit process mapping variables truncating tracking outputs executing payload generation configurations parsing limit blocks matching tracking process capacities mapping bounding targets executing strings mapping parameter capacity arrays defining bounds securely determining execution limit outputs rendering targets defining outputs.</summary>
    [JsonPropertyName("MaxDiffCharacters")]
    public int MaxDiffCharacters { get; init; } = 12000;

    /// <summary>Truncates string constraints executing memory targets safely bounding output variable tracking mapping parameters safely establishing limits identifying strings identifying constraints determining paths defining generation variables isolating execution strings securely bounding explicit file loops defining capacity loops matching explicit file targets bounding paths matching specific capacity bounding parameters isolating target path execution schemas defining variables bounding strings defining paths.</summary>
    [JsonPropertyName("MaxAttachmentCharacters")]
    public int MaxAttachmentCharacters { get; init; } = 8000;
}

/// <summary>AOT-friendly serialization context supporting <see cref="AppConfig"/> metadata extraction defining explicit target configuration mapping bounds parsing paths securely defining logic tracking capabilities.</summary>
[JsonSerializable(typeof(AppConfig))]
public sealed partial class AppConfigSerializerContext : JsonSerializerContext;

/// <summary>AOT-friendly serialization context determining <see cref="ModelResponse"/> extraction logic defining explicit network output mapping paths parsing JSON capacities explicitly.</summary>
[JsonSerializable(typeof(ModelResponse))]
public sealed partial class ModelResponseSerializerContext : JsonSerializerContext;

/// <summary>AOT-friendly serialization context determining CLI startup payloads validating <see cref="CliOptions"/> tracking limits defining target boundaries rendering execution logic paths.</summary>
[JsonSerializable(typeof(CliOptions))]
public sealed partial class CliOptionsSerializerContext : JsonSerializerContext;

/// <summary>AOT-friendly serialization context processing interaction configurations parsing limits explicitly mapping <see cref="ReplConfig"/> arrays rendering capacities tracking boundary loops safely determining variables isolating variables.</summary>
[JsonSerializable(typeof(ReplConfig))]
public sealed partial class ReplConfigSerializerContext : JsonSerializerContext;

/// <summary>AOT-friendly serialization context defining <see cref="GitToolConfig"/> parsing executing string targets configuring system policy boundaries isolating tracking generation arrays dynamically tracking outputs.</summary>
[JsonSerializable(typeof(GitToolConfig))]
public sealed partial class GitToolConfigSerializerContext : JsonSerializerContext;

/// <summary>AOT-friendly serialization context parsing <see cref="CommitMessageCacheFile"/> payloads executing string constraints parsing mapping boundary schemas generating caching tracking parameters safely rendering outputs.</summary>
[JsonSerializable(typeof(CommitMessageCacheFile), GenerationMode = JsonSourceGenerationMode.Metadata)]
public sealed partial class CommitMessageCacheFileSerializerContext : JsonSerializerContext;

static partial class RegexExtensions
{
    [GeneratedRegex("^[a-z]+(\\([^)]+\\))?!?: .+", RegexOptions.Compiled)]
    public static partial Regex SplitLineRegex();
}