using System.Text;

namespace Gatekeeper.Infrastructure.Services;

/// <summary>Assembly-owned Lua, independent of consumer working/publish directories.</summary>
internal static class RedisScriptSource
{
    // Creating the Lazy performs no I/O in a service type initializer. Missing resources
    // surface directly with script/resource context when the script is first needed.
    internal static Lazy<string> Load(string name) => new(() => Read(name),
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static string Read(string name)
    {
        var resource = "Gatekeeper.Infrastructure.Scripts." + name;
        using var stream = typeof(RedisScriptSource).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException(
                $"Redis script '{name}' is missing embedded resource '{resource}' in Gatekeeper.Infrastructure.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var source = reader.ReadToEnd();
        if (string.IsNullOrWhiteSpace(source))
            throw new InvalidOperationException($"Redis script '{name}' has an empty embedded resource '{resource}'.");
        return source;
    }
}
