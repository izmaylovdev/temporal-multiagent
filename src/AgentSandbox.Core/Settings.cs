namespace AgentSandbox.Core;

/// <summary>Central configuration, read live from the environment.</summary>
public static class Settings
{
    private static string Env(string key, string def = "") =>
        Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : def;

    private static int EnvInt(string key, int def) =>
        int.TryParse(Environment.GetEnvironmentVariable(key), out var v) ? v : def;

    // LLM
    public static string AnthropicApiKey => Env("ANTHROPIC_API_KEY");
    public static string AnthropicModel => Env("ANTHROPIC_MODEL", "claude-haiku-4-5-20251001");
    public static string AnthropicBaseUrl => Env("ANTHROPIC_BASE_URL", "https://api.anthropic.com");

    // Temporal
    public static string TemporalAddress => Env("TEMPORAL_ADDRESS", "temporal:7233");
    public static string TemporalNamespace => Env("TEMPORAL_NAMESPACE", "default");
    public static string TaskQueue => Env("TEMPORAL_TASK_QUEUE", "agent-sandbox");

    // Agent loop
    public static int MaxIterations => EnvInt("MAX_ITERATIONS", 4);
    public static int WorkflowTimeoutSeconds => EnvInt("WORKFLOW_TIMEOUT_SECONDS", 600);

    // Sandbox
    public static string SandboxRuntime => Env("SANDBOX_RUNTIME", "auto");
    public static string SandboxImage => Env("SANDBOX_IMAGE", "agent-sandbox:latest");
    public static string SandboxMemory => Env("SANDBOX_MEMORY", "512m");
    public static string SandboxCpus => Env("SANDBOX_CPUS", "1.0");
    public static int SandboxPids => EnvInt("SANDBOX_PIDS", 128);
    public static int SandboxTimeoutSeconds => EnvInt("SANDBOX_TIMEOUT_SECONDS", 30);
    public static int SandboxOutputMaxBytes => EnvInt("SANDBOX_OUTPUT_MAX_BYTES", 131072);

    // Uploads
    public static long MaxFileBytes => EnvInt("MAX_FILE_BYTES", 10 * 1024 * 1024);
    public static long MaxTotalUploadBytes => EnvInt("MAX_TOTAL_UPLOAD_BYTES", 25 * 1024 * 1024);
    public static readonly string[] AllowedExtensions = { ".csv", ".tsv", ".json", ".txt", ".parquet", ".xlsx" };

    // Run storage. RunsDir is the path inside the api/worker containers; RunsDirHost is the SAME
    // directory on the Docker host, used when launching sandbox child containers.
    public static string RunsDir => Env("RUNS_DIR", "/data/runs");
    public static string RunsDirHost => Env("RUNS_DIR_HOST", "./.runs");

    // Langfuse
    public static string LangfuseHost => Env("LANGFUSE_HOST");
    public static string LangfusePublicKey => Env("LANGFUSE_PUBLIC_KEY");
    public static string LangfuseSecretKey => Env("LANGFUSE_SECRET_KEY");

    public static bool LangfuseEnabled =>
        LangfuseHost.Length > 0 && LangfusePublicKey.Length > 0 && LangfuseSecretKey.Length > 0;
}
