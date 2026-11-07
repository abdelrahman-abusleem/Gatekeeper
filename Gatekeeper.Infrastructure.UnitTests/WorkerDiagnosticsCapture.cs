using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Gatekeeper.Infrastructure.Diagnostics;
using Gatekeeper.Infrastructure.Services;
using Gatekeeper.Infrastructure.Workers;
using Microsoft.Extensions.Logging;

namespace Gatekeeper.Infrastructure.UnitTests;

internal sealed class WorkerDiagnosticsCapture : ILogger<QueueProcessorWorker>, ILogger<RedisAdmissionClaimStore>, IDisposable
{
    private readonly AsyncLocal<bool> _capture = new();
    private readonly MeterListener _listener = new();
    public ConcurrentQueue<(string? Category, long Value)> Failures { get; } = new();
    public ConcurrentQueue<(LogLevel Level, string Message)> Logs { get; } = new();

    public WorkerDiagnosticsCapture()
    {
        _capture.Value = true;
        var failures = AdmissionMetrics.Failures;
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument, failures)) listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            if (!_capture.Value) return;
            foreach (var tag in tags)
                if (tag.Key == AdmissionMetricTags.Category) Failures.Enqueue((tag.Value?.ToString(), value));
        });
        _listener.Start();
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        if (state is IEnumerable<KeyValuePair<string, object?>> properties)
            message += " " + string.Join(" ", properties.Select(property => property.Value));
        Logs.Enqueue((logLevel, message));
    }

    public void Dispose()
    {
        _capture.Value = false;
        _listener.Dispose();
    }
}
