using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

#nullable enable

namespace RemotePartyFinderReborn;

[Serializable]
[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
internal sealed class UploadablePartyDetail {
    public uint ListingId { get; set; }
    public ulong LeaderContentId { get; set; }
    public string LeaderName { get; set; } = string.Empty;
    public ushort HomeWorld { get; set; }
    public List<ulong> MemberContentIds { get; set; } = new();
    public List<byte> MemberJobs { get; set; } = new();
    public List<string> SlotFlags { get; set; } = new();
}
