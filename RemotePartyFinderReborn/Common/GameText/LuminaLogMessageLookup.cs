using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Lumina.Text.ReadOnly;
using LuminaLogMessage = Lumina.Excel.Sheets.LogMessage;

#nullable enable

namespace RemotePartyFinderReborn;

internal sealed class LuminaLogMessageLookup : ILogMessageLookup {
    private readonly IDataManager _dataManager;

    internal LuminaLogMessageLookup(IDataManager dataManager) {
        _dataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));
    }

    public bool TryGet(uint logMessageId, out LogMessageInfo message) {
        var sheet = _dataManager.GetExcelSheet<LuminaLogMessage>();
        if (!sheet.TryGetRow(logMessageId, out var row)) {
            message = default;
            return false;
        }

        message = ToInfo(row);
        return true;
    }

    public IEnumerable<LogMessageInfo> Search(Func<LogMessageInfo, bool> predicate) {
        ArgumentNullException.ThrowIfNull(predicate);

        var sheet = _dataManager.GetExcelSheet<LuminaLogMessage>();
        foreach (var row in sheet) {
            var message = ToInfo(row);
            if (predicate(message)) {
                yield return message;
            }
        }
    }

    private static LogMessageInfo ToInfo(LuminaLogMessage row) {
        return new LogMessageInfo(row.RowId, row.Text.ExtractText());
    }
}
