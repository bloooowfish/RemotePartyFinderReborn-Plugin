using System;
using System.Collections.Generic;

#nullable enable

namespace RemotePartyFinderReborn;

internal interface ILogMessageLookup {
    bool TryGet(uint logMessageId, out LogMessageInfo message);
    IEnumerable<LogMessageInfo> Search(Func<LogMessageInfo, bool> predicate);
}
