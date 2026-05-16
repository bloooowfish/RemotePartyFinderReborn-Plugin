using System;
using Dalamud.Game.Chat;
using Dalamud.Plugin.Services;

#nullable enable

namespace RemotePartyFinderReborn;

internal sealed class DalamudGameLogSuppressionRuntime : IDisposable {
    private readonly IChatGui _chatGui;
    private readonly Func<uint, bool> _shouldSuppress;
    private readonly Action<string>? _warningSink;
    private readonly Action<string>? _debugSink;
    private readonly string _sourceName;
    private readonly IChatGui.OnLogMessageDelegate _logMessageHandler;
    private bool _disposed;

    internal DalamudGameLogSuppressionRuntime(
        IChatGui chatGui,
        Func<uint, bool> shouldSuppress,
        string sourceName,
        Action<string>? warningSink = null,
        Action<string>? debugSink = null
    ) {
        _chatGui = chatGui ?? throw new ArgumentNullException(nameof(chatGui));
        _shouldSuppress = shouldSuppress ?? throw new ArgumentNullException(nameof(shouldSuppress));
        _sourceName = string.IsNullOrWhiteSpace(sourceName) ? nameof(DalamudGameLogSuppressionRuntime) : sourceName;
        _warningSink = warningSink;
        _debugSink = debugSink;
        _logMessageHandler = OnLogMessage;
        _chatGui.LogMessage += _logMessageHandler;
    }

    public void Dispose() {
        if (_disposed) {
            return;
        }

        _disposed = true;
        _chatGui.LogMessage -= _logMessageHandler;
    }

    private void OnLogMessage(ILogMessage message) {
        try {
            if (_shouldSuppress(message.LogMessageId)) {
                message.PreventOriginal();
                _debugSink?.Invoke($"{_sourceName}: suppressed ChatLog.{message.LogMessageId}.");
            }
        } catch (Exception exception) {
            _warningSink?.Invoke($"{_sourceName}: failed to process chat log suppression event. {exception.Message}");
        }
    }
}
