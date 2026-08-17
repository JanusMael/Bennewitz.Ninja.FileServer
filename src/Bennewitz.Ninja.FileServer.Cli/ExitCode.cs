namespace Bennewitz.Ninja.FileServer;

/// <summary>
/// Exit codes returned to the container runtime on process termination.
/// </summary>
/// <remarks>
/// Docker and Kubernetes inspect the exit code to decide whether to restart the container.
/// Non-zero generally triggers a restart under the configured restart policy; distinguishing
/// <em>why</em> the process exited lets operators and orchestrators make smarter decisions:
/// a configuration error will keep crashing until the config is fixed, whereas a runtime
/// crash may recover on its own.
/// <list type="table">
///   <listheader><term>Code</term><description>Meaning</description></listheader>
///   <item><term>0</term><description>Clean shutdown — SIGTERM received and gracefully handled.</description></item>
///   <item><term>1</term><description>Unhandled runtime exception — restart may recover a transient fault.</description></item>
///   <item><term>2</term><description>Configuration error — restart will not help; fix the settings first.</description></item>
///   <item><term>3</term><description>Environment not ready — fix volume mounts or paths, then restart.</description></item>
/// </list>
/// </remarks>
public static class ExitCode
{
    /// <summary>
    /// Clean shutdown. SIGTERM was received and Kestrel drained its connections
    /// before the process exited. No operator action required.
    /// </summary>
    public const int Success = 0;

    /// <summary>
    /// Unhandled exception on a background thread or in the request pipeline after
    /// startup completed. Restarting the container may recover a transient fault.
    /// </summary>
    public const int UnhandledException = 1;

    /// <summary>
    /// Missing or invalid configuration. <c>ServedFilesRoot</c> is absent or not an
    /// absolute path, a port value is out of range, or a required setting cannot be
    /// resolved from <c>settings.json</c> or environment variables.
    /// Restarting the container will not help — correct the configuration first.
    /// </summary>
    public const int ConfigurationError = 2;

    /// <summary>
    /// Environment not ready. The directory referenced by <c>ServedFilesRoot</c> or
    /// the certificate file referenced by <c>CertificatePath</c> does not exist at
    /// the configured path. Check volume mounts and bind paths, then restart.
    /// </summary>
    public const int EnvironmentError = 3;
}
