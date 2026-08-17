using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Bennewitz.Ninja.FileServer;

/// <summary>
/// Static configuration facade for the file server. Call <see cref="Initialize"/> once at
/// process startup before accessing any property.
/// </summary>
/// <remarks>
/// Settings are resolved in this order — each layer overrides the previous:
/// <list type="number">
///   <item><description><c>settings.json</c> — located next to the executable (optional)</description></item>
///   <item><description>Environment variables — override JSON values</description></item>
///   <item><description>Command-line arguments — override both; see <see cref="Initialize"/> for supported switches</description></item>
/// </list>
/// Any missing or invalid value for <see cref="ServedFilesRoot"/> causes a startup failure with a
/// descriptive message rather than silently falling back to the drive root.
/// HTTPS is enabled automatically when <see cref="CertificatePath"/> resolves to a value;
/// if no certificate is configured the server runs HTTP-only.
/// </remarks>
public static class Settings
{
    private static SettingsModel _settingsModel = new();
    private static string _moduleDirectory = string.Empty;
    private static IReadOnlySet<string> _allowedExtensions =
        new HashSet<string>(0, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Loads and merges settings from all sources in precedence order:
    /// <c>settings.json</c> → environment variables → <paramref name="args"/>.
    /// Must be called once before any property on this class is accessed.
    /// </summary>
    /// <param name="args">
    /// Command-line arguments from <see cref="Program.Main"/>. Recognised switches:
    /// <c>--root</c>, <c>--route</c>, <c>--http-port</c>, <c>--https-port</c>,
    /// <c>--cert</c>, <c>--cert-password</c>, <c>--allowed-extensions</c>.
    /// Pass <c>--help</c> (or <c>-h</c> / <c>-?</c>) to print usage and exit 0.
    /// Both <c>--key value</c> and <c>--key=value</c> forms are accepted.
    /// </param>
    internal static void Initialize(string[] args)
    {
        // Handle --help before any file I/O so it works without a valid settings.json.
        foreach (var a in args)
        {
            if (a is "--help" or "-h" or "-?")
            {
                PrintHelp();
                Environment.Exit(0);
            }
        }

        var mainModule = Process.GetCurrentProcess().MainModule
            ?? throw new InvalidOperationException(
                "Cannot determine the application directory: Process.MainModule is null.");

        _moduleDirectory = Path.GetDirectoryName(mainModule.FileName)
            ?? throw new InvalidOperationException(
                $"Cannot determine the directory containing '{mainModule.FileName}'.");

        var path = Path.Combine(_moduleDirectory, "settings.json");

        if (File.Exists(path))
        {
            _settingsModel = SettingsModel.Load(path);
            Console.WriteLine($"Loaded settings from `{path}`");
        }
        else
        {
            _settingsModel = new SettingsModel();
            Console.WriteLine($"No settings.json found at `{path}`. Starting with defaults.");
        }

        // Environment variables override settings.json values.
        var envRoot = Environment.GetEnvironmentVariable("FILE_SERVER_ROOT");
        if (!string.IsNullOrWhiteSpace(envRoot))
        {
            _settingsModel.ServedFilesRoot = envRoot;
            Console.WriteLine($"FILE_SERVER_ROOT overrides ServedFilesRoot: `{envRoot}`");
        }

        var envRoute = Environment.GetEnvironmentVariable("FILE_SERVER_ROUTE");
        if (!string.IsNullOrWhiteSpace(envRoute))
        {
            _settingsModel.ServedFilesRoute = envRoute;
            Console.WriteLine($"FILE_SERVER_ROUTE overrides ServedFilesRoute: `{envRoute}`");
        }

        var envPort = Environment.GetEnvironmentVariable("FILE_SERVER_HTTP_PORT");
        if (!string.IsNullOrWhiteSpace(envPort))
        {
            if (int.TryParse(envPort, out var port) && port is >= 1 and <= 65535)
            {
                _settingsModel.HttpPort = port;
                Console.WriteLine($"FILE_SERVER_HTTP_PORT overrides HttpPort: `{port}`");
            }
            else
            {
                Console.WriteLine(
                    $"WARNING: FILE_SERVER_HTTP_PORT value '{envPort}' is not a valid port (1-65535). " +
                    "The configured or default HttpPort will be used instead.");
            }
        }

        var envHttpsPort = Environment.GetEnvironmentVariable("FILE_SERVER_HTTPS_PORT");
        if (!string.IsNullOrWhiteSpace(envHttpsPort))
        {
            if (int.TryParse(envHttpsPort, out var port) && port is >= 1 and <= 65535)
            {
                _settingsModel.HttpsPort = port;
                Console.WriteLine($"FILE_SERVER_HTTPS_PORT overrides HttpsPort: `{port}`");
            }
            else
            {
                Console.WriteLine(
                    $"WARNING: FILE_SERVER_HTTPS_PORT value '{envHttpsPort}' is not a valid port (1-65535). " +
                    "The configured or default HttpsPort will be used instead.");
            }
        }

        var envCertPath = Environment.GetEnvironmentVariable("FILE_SERVER_CERT_PATH");
        if (!string.IsNullOrWhiteSpace(envCertPath))
        {
            _settingsModel.CertificatePath = envCertPath;
            Console.WriteLine($"FILE_SERVER_CERT_PATH overrides CertificatePath: `{envCertPath}`");
        }

        var envCertPassword = Environment.GetEnvironmentVariable("FILE_SERVER_CERT_PASSWORD");
        if (envCertPassword is not null) // allow empty string (passwordless PFX)
        {
            _settingsModel.CertificatePassword = envCertPassword;
            Console.WriteLine("FILE_SERVER_CERT_PASSWORD overrides CertificatePassword.");
        }

        var envExtensions = Environment.GetEnvironmentVariable("FILE_SERVER_ALLOWED_EXTENSIONS");
        if (!string.IsNullOrWhiteSpace(envExtensions))
        {
            _settingsModel.AllowedExtensions = envExtensions
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Console.WriteLine($"FILE_SERVER_ALLOWED_EXTENSIONS overrides AllowedExtensions: `{envExtensions}`");
        }

        // CLI arguments override everything.
        ApplyArgs(args, _settingsModel);

        // If no root was supplied from any source, show help and exit cleanly so the
        // first-time experience is informative rather than a fatal configuration error.
        if (string.IsNullOrWhiteSpace(_settingsModel.ServedFilesRoot))
        {
            PrintHelp();

            var examplePath = Path.Combine(_moduleDirectory, "settings.json.example");
            if (File.Exists(examplePath))
            {
                Console.WriteLine();
                Console.WriteLine($"An example configuration file is available at:");
                Console.WriteLine($"  {examplePath}");
                Console.WriteLine("Rename it to settings.json and set ServedFilesRoot to get started.");
            }

            Environment.Exit(0);
        }

        _allowedExtensions = NormalizeExtensions(_settingsModel.AllowedExtensions);
    }

    /// <summary>
    /// The HTTP port Kestrel listens on. Default: <c>5550</c>.
    /// Override with environment variable <c>FILE_SERVER_HTTP_PORT</c>, the <c>HttpPort</c> key in
    /// <c>settings.json</c>, or the <c>--http-port</c> CLI argument.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown at first access if the configured value is outside 1–65535.</exception>
    public static int HttpPort => ValidatePort(_settingsModel.HttpPort ?? 5550, nameof(HttpPort));

    /// <summary>
    /// The HTTPS port Kestrel listens on when a certificate is configured. Default: <c>5551</c>.
    /// Override with environment variable <c>FILE_SERVER_HTTPS_PORT</c>, the <c>HttpsPort</c> key
    /// in <c>settings.json</c>, or the <c>--https-port</c> CLI argument.
    /// Has no effect when no certificate is configured.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown at first access if the configured value is outside 1–65535.</exception>
    public static int HttpsPort => ValidatePort(_settingsModel.HttpsPort ?? 5551, nameof(HttpsPort));

    /// <summary>
    /// The absolute path to a PFX certificate file used to enable HTTPS. When set, Kestrel listens
    /// on <see cref="HttpsPort"/> in addition to <see cref="HttpPort"/>, and HTTP requests are
    /// redirected to HTTPS. When absent, the server runs HTTP-only.
    /// </summary>
    /// <remarks>
    /// Override with environment variable <c>FILE_SERVER_CERT_PATH</c>, the <c>CertificatePath</c>
    /// key in <c>settings.json</c>, or the <c>--cert</c> CLI argument.
    /// </remarks>
    public static string? CertificatePath => string.IsNullOrWhiteSpace(_settingsModel.CertificatePath)
        ? null
        : _settingsModel.CertificatePath;

    /// <summary>
    /// The password for the PFX certificate at <see cref="CertificatePath"/>. May be empty for
    /// password-less PFX files.
    /// </summary>
    /// <remarks>
    /// Override with environment variable <c>FILE_SERVER_CERT_PASSWORD</c>, the
    /// <c>CertificatePassword</c> key in <c>settings.json</c>, or the <c>--cert-password</c> CLI argument.
    /// </remarks>
    public static string? CertificatePassword => _settingsModel.CertificatePassword;

    /// <summary>
    /// <c>true</c> when a certificate path is configured and HTTPS should be enabled.
    /// </summary>
    [MemberNotNullWhen(true, nameof(CertificatePath))]
    public static bool HasHttpsCertificate => CertificatePath is not null;

    /// <summary>
    /// The absolute path to the directory whose contents are served. Must be a fully-qualified,
    /// rooted path (e.g. <c>/srv/files</c> on Linux or <c>C:\Share</c> on Windows).
    /// </summary>
    /// <remarks>
    /// Override with environment variable <c>FILE_SERVER_ROOT</c>, the <c>ServedFilesRoot</c> key
    /// in <c>settings.json</c>, or the <c>--root</c> CLI argument. The application fails at startup
    /// if this value is absent or not an absolute path.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown at first access if <c>ServedFilesRoot</c> is not configured or is not an absolute path.
    /// </exception>
    public static string ServedFilesRoot
    {
        get
        {
            var configured = _settingsModel.ServedFilesRoot;

            if (string.IsNullOrWhiteSpace(configured))
                throw new InvalidOperationException(
                    "ServedFilesRoot is not configured. " +
                    "Set the FILE_SERVER_ROOT environment variable, add ServedFilesRoot to settings.json, " +
                    "or pass --root <path> on the command line.");

            if (!Path.IsPathRooted(configured))
                throw new InvalidOperationException(
                    $"ServedFilesRoot '{configured}' is not an absolute path. " +
                    "Provide a fully-qualified path (e.g. /srv/files or C:\\Share).");

            return configured;
        }
    }

    /// <summary>
    /// The URL path segment under which files are served (without leading slash). Default: <c>files</c>.
    /// Override with environment variable <c>FILE_SERVER_ROUTE</c>, the <c>ServedFilesRoute</c> key
    /// in <c>settings.json</c>, or the <c>--route</c> CLI argument.
    /// </summary>
    public static string ServedFilesRoute
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_settingsModel.ServedFilesRoute))
                return "files";

            // Strip any leading slash — callers prefix one when building request paths.
            return _settingsModel.ServedFilesRoute.TrimStart('/');
        }
    }

    /// <summary>
    /// The set of file extensions that may be served and listed. When empty (default) all files
    /// are served. Extensions are compared case-insensitively; the leading dot is optional in
    /// configuration but normalised internally (e.g. <c>pdf</c> and <c>.pdf</c> are equivalent).
    /// </summary>
    /// <remarks>
    /// Configure via <c>AllowedExtensions</c> in <c>settings.json</c> (JSON string array, e.g.
    /// <c>[".pdf", ".txt"]</c>), the environment variable <c>FILE_SERVER_ALLOWED_EXTENSIONS</c>
    /// (semicolon-delimited, e.g. <c>.pdf;.txt;.zip</c>), or the <c>--allowed-extensions</c>
    /// CLI argument (same semicolon-delimited format).
    /// Files without an extension are hidden when a filter is active; to include them add an
    /// empty string <c>""</c> to the JSON array.
    /// </remarks>
    public static IReadOnlySet<string> AllowedExtensions => _allowedExtensions;

    // ASP.NET Core host-configuration switches consumed by WebApplication.CreateBuilder(args).
    // Warnings are suppressed for these so users don't see false-positive "unrecognised" noise.
    private static readonly HashSet<string> s_frameworkArgs = new(StringComparer.OrdinalIgnoreCase)
    {
        "--urls", "--environment", "--applicationName", "--contentRoot", "--pathBase"
    };

    /// <summary>
    /// Applies CLI switches from <paramref name="args"/> to <paramref name="model"/>,
    /// overriding any values already set by settings.json or environment variables.
    /// Supports <c>--key value</c> (space-separated) and <c>--key=value</c> (equals) forms.
    /// Unrecognised <c>--</c> switches emit a stderr warning but are not fatal;
    /// ASP.NET Core consumes its own args (<c>--urls</c>, <c>--environment</c>, etc.)
    /// after this method returns.
    /// </summary>
    private static void ApplyArgs(string[] args, SettingsModel model)
    {
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            // Split --key=value form; leave key/inlineValue for space-separated form.
            string key;
            string? inlineValue;
            var eqIdx = arg.IndexOf('=');
            if (eqIdx > 0)
            {
                key         = arg[..eqIdx];
                inlineValue = arg[(eqIdx + 1)..];
            }
            else
            {
                key         = arg;
                inlineValue = null;
            }

            if (key is "--help" or "-h" or "-?") continue; // handled before Initialize body runs

            // Skip positional tokens (values consumed by previous iterations, or framework args).
            if (!key.StartsWith('-')) continue;

            // For space-separated form: consume the next token if it is not itself a flag.
            // Captures i by reference so the loop counter advances past the consumed value.
            string? TakeNext() =>
                inlineValue is not null       ? inlineValue
                : i + 1 < args.Length && !args[i + 1].StartsWith('-') ? args[++i]
                : null;

            switch (key)
            {
                case "--root":
                {
                    var val = TakeNext();
                    if (val is not null)
                    {
                        model.ServedFilesRoot = val;
                        Console.WriteLine($"--root overrides ServedFilesRoot: `{val}`");
                    }
                    break;
                }
                case "--route":
                {
                    var val = TakeNext();
                    if (val is not null)
                    {
                        model.ServedFilesRoute = val;
                        Console.WriteLine($"--route overrides ServedFilesRoute: `{val}`");
                    }
                    break;
                }
                case "--http-port":
                {
                    var raw = TakeNext();
                    if (int.TryParse(raw, out var port) && port is >= 1 and <= 65535)
                    {
                        model.HttpPort = port;
                        Console.WriteLine($"--http-port overrides HttpPort: `{port}`");
                    }
                    else
                    {
                        Console.Error.WriteLine(
                            $"WARNING: --http-port value '{raw}' is not a valid port (1-65535). Ignored.");
                    }
                    break;
                }
                case "--https-port":
                {
                    var raw = TakeNext();
                    if (int.TryParse(raw, out var port) && port is >= 1 and <= 65535)
                    {
                        model.HttpsPort = port;
                        Console.WriteLine($"--https-port overrides HttpsPort: `{port}`");
                    }
                    else
                    {
                        Console.Error.WriteLine(
                            $"WARNING: --https-port value '{raw}' is not a valid port (1-65535). Ignored.");
                    }
                    break;
                }
                case "--cert":
                {
                    var val = TakeNext();
                    if (val is not null)
                    {
                        model.CertificatePath = val;
                        Console.WriteLine($"--cert overrides CertificatePath: `{val}`");
                    }
                    break;
                }
                case "--cert-password":
                {
                    // Allow empty string (passwordless PFX); TakeNext() accepts "" since it
                    // doesn't start with '-'.
                    var val = TakeNext();
                    if (val is not null)
                    {
                        model.CertificatePassword = val;
                        Console.WriteLine("--cert-password overrides CertificatePassword.");
                    }
                    break;
                }
                case "--allowed-extensions":
                {
                    var raw = TakeNext();
                    if (raw is not null)
                    {
                        model.AllowedExtensions = raw.Split(';',
                            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        Console.WriteLine($"--allowed-extensions overrides AllowedExtensions: `{raw}`");
                    }
                    break;
                }
                default:
                    // Suppress warnings for known ASP.NET Core host-configuration switches that
                    // WebApplication.CreateBuilder(args) consumes after this method returns.
                    if (key.StartsWith("--") && !s_frameworkArgs.Contains(key))
                        Console.Error.WriteLine($"WARNING: Unrecognised argument '{key}'. Ignored.");
                    break;
            }
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Bennewitz.Ninja.FileServer — serve a local directory over HTTP/HTTPS");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  FileServer [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --root <path>                Absolute path to the directory to serve (required)");
        Console.WriteLine("  --route <segment>            URL path segment for the file browser (default: files)");
        Console.WriteLine("  --http-port <port>           HTTP listen port (default: 5550)");
        Console.WriteLine("  --https-port <port>          HTTPS listen port when --cert is set (default: 5551)");
        Console.WriteLine("  --cert <path>                Path to a PFX certificate file (enables HTTPS)");
        Console.WriteLine("  --cert-password <password>   Password for the PFX file (omit for passwordless)");
        Console.WriteLine("  --allowed-extensions <exts>  Semicolon-delimited allowed extensions, e.g. .pdf;.txt");
        Console.WriteLine("  --help, -h, -?               Show this help and exit");
        Console.WriteLine();
        Console.WriteLine("Precedence (highest wins): CLI args > environment variables > settings.json");
        Console.WriteLine();
        Console.WriteLine("Environment variables:");
        Console.WriteLine("  FILE_SERVER_ROOT              ServedFilesRoot");
        Console.WriteLine("  FILE_SERVER_ROUTE             ServedFilesRoute");
        Console.WriteLine("  FILE_SERVER_HTTP_PORT         HttpPort");
        Console.WriteLine("  FILE_SERVER_HTTPS_PORT        HttpsPort");
        Console.WriteLine("  FILE_SERVER_CERT_PATH         CertificatePath");
        Console.WriteLine("  FILE_SERVER_CERT_PASSWORD     CertificatePassword");
        Console.WriteLine("  FILE_SERVER_ALLOWED_EXTENSIONS  Semicolon-delimited, e.g. .pdf;.txt");
    }

    private static IReadOnlySet<string> NormalizeExtensions(string[]? raw)
    {
        if (raw is null || raw.Length == 0)
            return new HashSet<string>(0, StringComparer.OrdinalIgnoreCase);
        var set = new HashSet<string>(raw.Length, StringComparer.OrdinalIgnoreCase);
        foreach (var ext in raw)
        {
            var t = ext.Trim();
            // Empty string represents files with no extension (Path.GetExtension returns "").
            // Don't prepend a dot — just add the empty string to the set as-is.
            if (t.Length == 0 || t.StartsWith('.'))
                set.Add(t);
            else
                set.Add('.' + t);
        }
        return set;
    }

    private static int ValidatePort(int port, string name)
    {
        if (port is < 1 or > 65535)
            throw new InvalidOperationException(
                $"{name} value {port} is not a valid port number (1–65535). " +
                $"Update {name} in settings.json or pass --{name.ToLowerInvariant().Replace("port", "-port")} on the command line.");
        return port;
    }

    [SuppressMessage("ReSharper", "MemberHidesStaticFromOuterClass")]
    private sealed class SettingsModel
    {
        public string? ServedFilesRoot { get; set; } = string.Empty;

        public string? ServedFilesRoute { get; set; } = string.Empty;

        public int? HttpPort { get; set; }
        public int? HttpsPort { get; set; }

        public string? CertificatePath { get; set; }
        public string? CertificatePassword { get; set; }

        public string[]? AllowedExtensions { get; set; }

        public static SettingsModel Load(string filePath)
        {
            try
            {
                var json = File.ReadAllText(filePath);
                var settings = JsonSerializer.Deserialize<SettingsModel>(json);
                return settings ?? new SettingsModel();
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"settings.json contains invalid JSON: {ex.Message}", ex);
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException(
                    $"Could not read settings.json: {ex.Message}", ex);
            }
        }
    }
}
