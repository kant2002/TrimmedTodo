using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Benchmarks;

internal class ProjectBuilder
{
    private static readonly string _dotnetFileName = "dotnet" + (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : "");

    private PublishResult? _publishResult = null;
    private long? _appStarted = null;

    public ProjectBuilder(string projectName, PublishScenario scenario)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(projectName);

        ProjectName = projectName;
        PublishScenario = scenario;
    }

    public string ProjectName { get; }

    public PublishScenario PublishScenario { get; }

    /// <summary>
    /// The size of the published app in bytes.
    /// </summary>
    public int? AppPublishSize { get; }

    /// <summary>
    /// The working set of the app before it shutdown.
    /// </summary>
    public int? AppMemorySize { get; }

    /// <summary>
    /// The app process.
    /// </summary>
    public Process? AppProcess { get; private set; }

    public void Publish()
    {
        var runId = Enum.GetName(PublishScenario);
        _publishResult = PublishScenario switch
        {
            PublishScenario.Default => Publish(ProjectName, runId: runId),
            PublishScenario.NoAppHost => Publish(ProjectName, useAppHost: false, runId: runId),
            PublishScenario.ReadyToRun => Publish(ProjectName, readyToRun: true, runId: runId),
            PublishScenario.SelfContained => Publish(ProjectName, selfContained: true, trimLevel: TrimLevel.None, runId: runId),
            PublishScenario.SelfContainedReadyToRun => Publish(ProjectName, selfContained: true, readyToRun: true, trimLevel: TrimLevel.None, runId: runId),
            PublishScenario.SingleFile => Publish(ProjectName, selfContained: true, singleFile: true, trimLevel: TrimLevel.None, runId: runId),
            PublishScenario.SingleFileReadyToRun => Publish(ProjectName, selfContained: true, singleFile: true, readyToRun: true, trimLevel: TrimLevel.None, runId: runId),
            PublishScenario.Trimmed => Publish(ProjectName, selfContained: true, singleFile: true, trimLevel: GetTrimLevel(ProjectName), runId: runId),
            PublishScenario.TrimmedReadyToRun => Publish(ProjectName, selfContained: true, singleFile: true, readyToRun: true, trimLevel: GetTrimLevel(ProjectName), runId: runId),
            PublishScenario.AOT => PublishAot(ProjectName, trimLevel: GetTrimLevel(ProjectName), runId: runId),
            _ => throw new ArgumentException("Unrecognized publish scenario", nameof(PublishScenario))
        };
    }

    public void Run()
    {
        if (_publishResult is null)
        {
            throw new InvalidOperationException($"Project must be published first by calling '{nameof(Publish)}'.");
        }

        var appExePath = _publishResult.AppFilePath;
        if (!File.Exists(appExePath))
        {
            throw new ArgumentException($"Could not find application exe '{appExePath}'", nameof(appExePath));
        }

        var isAppHost = !Path.GetExtension(appExePath)!.Equals(".dll", StringComparison.OrdinalIgnoreCase);

        var process = new Process
        {
            StartInfo =
            {
                FileName = isAppHost ? appExePath : _dotnetFileName,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(appExePath),
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        if (!isAppHost)
        {
            process.StartInfo.ArgumentList.Add(appExePath);
        }

        var envVars = GetEnvVars();
        foreach (var (name, value) in envVars)
        {
            process.StartInfo.Environment.Add(name, value);
        }

        if (!process.Start())
        {
            HandleError(process, "Failed to start application process");
        }

        _appStarted = DateTime.UtcNow.Ticks;

        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            HandleError(process, $"Application process failed on exit ({process.ExitCode})");
        }

        static void HandleError(Process process, string message)
        {
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();

            var sb = new StringBuilder();
            sb.AppendLine(message);
            sb.AppendLine("Standard output:");
            sb.AppendLine(output);
            sb.AppendLine("Standard error:");
            sb.AppendLine(error);

            throw new InvalidOperationException(sb.ToString());
        }

        AppProcess = process;
    }

    public void SaveOutput()
    {
        if (_publishResult is null || AppProcess is null)
        {
            throw new InvalidOperationException($"Project must be published first by calling '{nameof(Publish)}' and then run by calling '{nameof(Run)}'.");
        }

        var outputFilePath = Path.Combine(Path.GetDirectoryName(_publishResult.AppFilePath)!, "output.txt");
        using var resultStream = new FileStream(outputFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        AppProcess.StandardOutput.BaseStream.CopyTo(resultStream);
    }

    private (string, string)[] GetEnvVars()
    {
        var result = new List<(string, string)> { ("SHUTDOWN_ON_START", "true") };

        if (_publishResult?.UserSecretsId is not null)
        {
            // Set env var for JWT signing key
            var userSecretsJsonPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "UserSecrets", _publishResult.UserSecretsId, "secrets.json");

            if (!File.Exists(userSecretsJsonPath))
            {
                throw new InvalidOperationException($"Could not find user secrets json file at path '{userSecretsJsonPath}'. " +
                    "Project has a UserSecretsId but has not been initialized for JWT authentication." +
                    "Please run 'dotnet user-jwts create' in the '$projectName' directory.");
            }

            var userSecretsJson = JsonDocument.Parse(File.OpenRead(userSecretsJsonPath));
            var configKeyName = "Authentication:Schemes:Bearer:SigningKeys";
            var jwtSigningKey = userSecretsJson.RootElement.GetProperty(configKeyName).EnumerateArray()
                .Single(o => o.GetProperty("Issuer").GetString() == "dotnet-user-jwts")
                .GetProperty("Value").GetString();

            if (jwtSigningKey is not null)
            {
                result.Add(("JWT_SIGNING_KEY", jwtSigningKey));
            }
        }

        return result.ToArray();
    }

    private static PublishResult Publish(
        string projectName,
        string configuration = "Release",
        bool selfContained = false,
        bool singleFile = false,
        bool readyToRun = false,
        bool useAppHost = true,
        TrimLevel trimLevel = TrimLevel.None,
        string? runId = null)
    {
        var args = new List<string>
        {
            "--runtime", RuntimeInformation.RuntimeIdentifier,
            selfContained || trimLevel != TrimLevel.None ? "--self-contained" : "--no-self-contained",
            $"-p:PublishSingleFile={(singleFile ? "true" : "")}",
            $"-p:PublishReadyToRun={(readyToRun ? "true" : "")}",
            "-p:PublishAot=false"
        };

        if (trimLevel != TrimLevel.None)
        {
            args.Add("-p:PublishTrimmed=true");
            args.Add($"-p:TrimMode={GetTrimLevelPropertyValue(trimLevel)}");
        }
        else
        {
            args.Add("-p:PublishTrimmed=false");
        }

        if (!useAppHost)
        {
            args.Add("-p:UseAppHost=false");
        }

        return PublishImpl(projectName, configuration, args, runId);
    }

    private readonly static List<string> _projectsSupportingAot = new()
    {
        "HelloWorld.Console",
        "HelloWorld.Web",
        "HelloWorld.Web.Stripped",
        "HelloWorld.HttpListener",
        "TrimmedTodo.Console.ApiClient"
    };

    private static PublishResult PublishAot(
        string projectName,
        string configuration = "Release",
        TrimLevel trimLevel = TrimLevel.Default,
        string? runId = null)
    {
        if (!_projectsSupportingAot.Contains(projectName))
        {
            throw new NotSupportedException($"The project '{projectName}' does support publishing for AOT.");
        }

        if (trimLevel == TrimLevel.None)
        {
            throw new ArgumentOutOfRangeException(nameof(trimLevel), "'TrimLevel.None' is not supported when publishing for AOT.");
        }

        var args = new List<string>
        {
            "--runtime", RuntimeInformation.RuntimeIdentifier,
            "--self-contained",
            "-p:PublishAot=true",
            "-p:PublishSingleFile=",
            "-p:PublishTrimmed="
        };

        if (trimLevel != TrimLevel.None)
        {
            args.Add($"-p:TrimMode={GetTrimLevelPropertyValue(trimLevel)}");
        }

        return PublishImpl(projectName, configuration, args, runId);
    }

    private static PublishResult PublishImpl(string projectName, string configuration = "Release", IEnumerable<string>? args = null, string? runId = null)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(projectName);

        var projectPath = Path.Combine(PathHelper.ProjectsDir, projectName, projectName + ".csproj");

        if (!File.Exists(projectPath))
        {
            throw new ArgumentException($"Project at '{projectPath}' could not be found", nameof(projectName));
        }

        runId ??= Random.Shared.NextInt64().ToString();
        var output = PathHelper.GetProjectPublishDir(projectName, runId);

        var cmdArgs = new List<string>
        {
            projectPath,
            $"--configuration", configuration
        };

        //DotNetCli.Clean(cmdArgs);

        cmdArgs.AddRange(new[] { $"--output", output });
        cmdArgs.Add("--disable-build-servers");
        if (args is not null)
        {
            cmdArgs.AddRange(args);
        }

        DotNetCli.Publish(cmdArgs);

        var appFilePath = Path.Join(output, projectName);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            appFilePath += ".exe";
        }

        if (!File.Exists(appFilePath))
        {
            appFilePath = Path.Join(output, projectName + ".dll");
            if (!File.Exists(appFilePath))
            {
                throw new InvalidOperationException($"Could not find application exe or dll '{appFilePath}'");
            }
        }

        return new(appFilePath, GetUserSecretsId(projectPath));
    }

    private static string? GetUserSecretsId(string projectFilePath)
    {
        var xml = XDocument.Load(projectFilePath);
        var userSecretsIdElement = xml.Descendants("UserSecretsId").FirstOrDefault();
        return userSecretsIdElement?.Value;
    }

    private static string GetTrimLevelPropertyValue(TrimLevel trimLevel)
    {
        return trimLevel switch
        {
            TrimLevel.Default => "",
            _ => Enum.GetName(trimLevel)?.ToLower() ?? ""
        };
    }

    private static TrimLevel GetTrimLevel(string projectName)
    {
        if (projectName.Contains("EfCore", StringComparison.OrdinalIgnoreCase)
            || projectName.Contains("Dapper", StringComparison.OrdinalIgnoreCase)
            || projectName.Contains("MinimalApi.Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            return TrimLevel.Partial;
        }

        if (projectName.Contains("Console"))
        {
            return TrimLevel.Default;
        }

        if (projectName.Contains("HelloWorld"))
        {
            return TrimLevel.Full;
        }

        return TrimLevel.Default;
    }
}

public record PublishResult(string AppFilePath, string? UserSecretsId = null);

public enum PublishScenario
{
    Default,
    NoAppHost,
    ReadyToRun,
    SelfContained,
    SelfContainedReadyToRun,
    SingleFile,
    SingleFileReadyToRun,
    Trimmed,
    TrimmedReadyToRun,
    AOT
}

enum TrimLevel
{
    None,
    Default,
    Partial,
    Full
}
