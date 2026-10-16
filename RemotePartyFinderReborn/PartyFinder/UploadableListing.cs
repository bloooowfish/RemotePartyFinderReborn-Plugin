using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Dalamud.Game.Gui.PartyFinder.Types;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace RemotePartyFinderReborn;

[Serializable]
[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
internal sealed class UploadableListing {
    public uint Id { get; }
    public uint ContentIdLower { get; }
    public byte[] Name { get; }
    public byte[] Description { get; }
    public ushort CreatedWorld { get; }
    public ushort HomeWorld { get; }
    public ushort CurrentWorld { get; }
    public DutyCategory Category { get; }
    public ushort Duty { get; }
    public DutyType DutyType { get; }
    public bool PfCategoryHighEnd { get; }
    public bool CfcExcelHighEnd { get; }
    public bool ClientHighEndDuty { get; }
    public bool BeginnersWelcome { get; }
    public ushort SecondsRemaining { get; }
    public ushort MinItemLevel { get; }
    public byte NumParties { get; }
    public byte SlotsAvailable { get; }
    public uint LastServerRestart { get; }
    public ObjectiveFlags Objective { get; }
    public ConditionFlags Conditions { get; }
    public DutyFinderSettingsFlags DutyFinderSettings { get; }
    public LootRuleFlags LootRules { get; }
    public SearchAreaFlags SearchArea { get; }
    public List<UploadableSlot> Slots { get; }
    public List<byte> JobsPresent { get; }

    internal UploadableListing(IPartyFinderListing source) {
        Id = NormalizeListingIdForUpload(source.Id);
        ContentIdLower = (uint)source.ContentId;
        Name = source.Name.Encode();
        Description = source.Description.Encode();
        CreatedWorld = (ushort)source.World.Value.RowId;
        HomeWorld = (ushort)source.HomeWorld.Value.RowId;
        CurrentWorld = (ushort)source.CurrentWorld.Value.RowId;
        Category = source.Category;
        Duty = source.RawDuty;
        DutyType = source.DutyType;
        PfCategoryHighEnd = IsPfCategoryHighEnd(source.Category);
        CfcExcelHighEnd = IsCfcExcelHighEnd(source);
        ClientHighEndDuty = IsClientHighEndDuty(PfCategoryHighEnd, CfcExcelHighEnd);
        BeginnersWelcome = source.BeginnersWelcome;
        SecondsRemaining = source.SecondsRemaining;
        MinItemLevel = source.MinimumItemLevel;
        NumParties = source.Parties;
        SlotsAvailable = source.SlotsAvailable;
        LastServerRestart = NormalizeLastPatchHotfixTimestamp(source.LastPatchHotfixTimestamp);
        Objective = source.Objective;
        Conditions = source.Conditions;
        DutyFinderSettings = source.DutyFinderSettings;
        LootRules = source.LootRules;
        SearchArea = source.SearchArea;
        Slots = source.Slots.Select(static slot => new UploadableSlot(slot)).ToList();
        JobsPresent = source.RawJobsPresent.ToList();
    }

    private static uint NormalizeLastPatchHotfixTimestamp(int timestamp) {
        return timestamp <= 0 ? 0U : (uint)timestamp;
    }

    internal static bool TryNormalizeListingIdForUpload(ulong sourceId, out uint listingId) {
        if (sourceId <= uint.MaxValue) {
            listingId = (uint)sourceId;
            return true;
        }

        listingId = 0;
        return false;
    }

    internal static uint NormalizeListingIdForUpload(ulong sourceId) {
        if (TryNormalizeListingIdForUpload(sourceId, out var listingId)) {
            return listingId;
        }

        throw new ArgumentOutOfRangeException(nameof(sourceId), sourceId, "Party finder listing id exceeds the upload contract range.");
    }

    internal static bool IsPfCategoryHighEnd(DutyCategory category) {
        return (category & DutyCategory.HighEndDuty) == DutyCategory.HighEndDuty;
    }

    internal static bool IsClientHighEndDuty(bool pfCategoryHighEnd, bool cfcExcelHighEnd) {
        return pfCategoryHighEnd || cfcExcelHighEnd;
    }

    internal static bool IsSourceClientHighEndDuty(IPartyFinderListing source) {
        ArgumentNullException.ThrowIfNull(source);
        return IsClientHighEndDuty(IsPfCategoryHighEnd(source.Category), IsCfcExcelHighEnd(source));
    }

    private static bool IsCfcExcelHighEnd(IPartyFinderListing source) {
        try {
            return source.Duty.ValueNullable?.HighEndDuty == true;
        } catch {
            // Optional telemetry must not block listing upload if Excel resolution is unavailable.
            return false;
        }
    }
}

[Serializable]
[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
internal sealed class UploadableSlot {
    private static readonly FieldInfo RawAcceptingField = typeof(PartyFinderSlot).GetField(
        "accepting",
        BindingFlags.Instance | BindingFlags.NonPublic
    ) ?? throw new MissingFieldException(typeof(PartyFinderSlot).FullName, "accepting");

    [JsonConverter(typeof(AcceptingFlagsJsonConverter))]
    public ulong Accepting { get; }

    internal UploadableSlot(PartyFinderSlot source) {
        ArgumentNullException.ThrowIfNull(source);
        // Dalamud's Accepting collection currently uses an indexer that casts JobFlags to uint.
        // That makes every flag above bit 31 (including Beastmaster) appear unset, so preserve
        // the raw mask stored by PartyFinderSlot instead of rebuilding it from that collection.
        Accepting = (ulong)(RawAcceptingField.GetValue(source)
            ?? throw new InvalidOperationException("PartyFinderSlot accepting flags were unavailable."));
    }
}

/// <summary>
/// Keeps the existing JSON number representation for masks understood by older servers while
/// encoding values that exceed UInt32 as fixed-width hexadecimal strings to avoid precision loss.
/// </summary>
internal sealed class AcceptingFlagsJsonConverter : JsonConverter {
    public override bool CanConvert(Type objectType) => objectType == typeof(ulong);

    public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer) {
        var flags = value is ulong raw
            ? raw
            : throw new JsonSerializationException("Expected a UInt64 accepting-job mask.");

        if (flags <= uint.MaxValue) {
            writer.WriteValue((uint)flags);
            return;
        }

        writer.WriteValue($"0x{flags:X16}");
    }

    public override object ReadJson(
        JsonReader reader,
        Type objectType,
        object existingValue,
        JsonSerializer serializer
    ) => throw new NotSupportedException("Accepting flags are only serialized for uploads.");

    public override bool CanRead => false;
}
