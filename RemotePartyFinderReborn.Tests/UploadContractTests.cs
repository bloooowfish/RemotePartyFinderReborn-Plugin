using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RemotePartyFinderReborn.Tomestone;
using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class UploadContractTests {
    static UploadContractTests() {
        DalamudAssemblyResolver.Register();
    }

    [Fact]
    public void Upload_models_use_ulong_listing_id_contract() {
        Assert.Equal(typeof(ulong), RequiredPropertyType<UploadableListing>(nameof(UploadableListing.Id)));
        Assert.Equal(typeof(ulong), RequiredPropertyType<UploadablePartyDetail>(nameof(UploadablePartyDetail.ListingId)));
        Assert.Equal(typeof(ulong), RequiredPropertyType<UploadablePartyDetailBatchItem>(nameof(UploadablePartyDetailBatchItem.ListingId)));
        Assert.Equal(typeof(ulong), RequiredPropertyType<ListingDetailCapability>(nameof(ListingDetailCapability.ListingId)));
    }

    [Fact]
    public void Listing_id_normalization_accepts_u64_up_to_int64_max_and_rejects_zero_or_overflow() {
        Assert.False(UploadableListing.TryNormalizeListingIdForUpload(0UL, out var zeroId));
        Assert.Equal(0UL, zeroId);
        Assert.Throws<ArgumentOutOfRangeException>(() => UploadableListing.NormalizeListingIdForUpload(0UL));

        const ulong largeListingId = (ulong)uint.MaxValue + 42UL;
        Assert.True(UploadableListing.TryNormalizeListingIdForUpload(largeListingId, out var normalizedLarge));
        Assert.Equal(largeListingId, normalizedLarge);
        Assert.Equal(largeListingId, UploadableListing.NormalizeListingIdForUpload(largeListingId));

        Assert.True(UploadableListing.TryNormalizeListingIdForUpload((ulong)long.MaxValue, out var maxSigned));
        Assert.Equal((ulong)long.MaxValue, maxSigned);

        var overflow = (ulong)long.MaxValue + 1UL;
        Assert.False(UploadableListing.TryNormalizeListingIdForUpload(overflow, out var overflowOut));
        Assert.Equal(0UL, overflowOut);
        Assert.Throws<ArgumentOutOfRangeException>(() => UploadableListing.NormalizeListingIdForUpload(overflow));
    }

    [Fact]
    public void Uploadable_party_detail_and_capabilities_preserve_large_u64_listing_ids() {
        const ulong largeListingId = 5_000_000_007UL;
        var detail = new UploadablePartyDetail {
            ListingId = largeListingId,
            LeaderContentId = 44UL,
            LeaderName = "Leader",
            HomeWorld = 77,
            CreatedWorld = 74,
            MemberContentIds = [44UL],
            MemberJobs = [19],
            SlotFlags = ["0x0000000000000001"],
        };

        var detailJson = JObject.Parse(JsonConvert.SerializeObject(detail));
        Assert.Equal(largeListingId, detailJson.Value<ulong>("listing_id"));

        var batchItem = UploadablePartyDetailBatchItem.FromDetail(detail, "tok-u64");
        var batchJson = JObject.Parse(JsonConvert.SerializeObject(batchItem));
        Assert.Equal(largeListingId, batchJson.Value<ulong>("listing_id"));

        var now = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var store = new DetailCapabilityStore();
        store.ApplyDetailCapabilities([
            new ListingDetailCapability {
                ListingId = largeListingId,
                Token = "cap-u64",
                ExpiresAt = now.AddMinutes(5).ToUnixTimeSeconds(),
            },
        ], now);

        Assert.False(store.ShouldDeferDetailRequest(largeListingId, now));
        Assert.True(store.TryGetDetailCapability(largeListingId, out var token, now));
        Assert.Equal("cap-u64", token);
    }

    [Fact]
    public void Uploadable_party_detail_and_batch_item_serialize_new_metadata_fields_in_snake_case() {
        var detail = new UploadablePartyDetail {
            ListingId = 9001U,
            LeaderContentId = 44UL,
            LeaderName = "Leader",
            HomeWorld = 77,
            CreatedWorld = 74,
            LastServerRestart = 1700000000U,
            ObservedAtUnixMs = 1770000000123L,
            MemberContentIds = [44UL],
            MemberJobs = [19],
            SlotFlags = ["0x0000000000000001"],
        };

        var detailJson = JObject.Parse(JsonConvert.SerializeObject(detail));
        Assert.Equal((ushort)74, detailJson.Value<ushort>("created_world"));
        Assert.Equal(1700000000U, detailJson.Value<uint>("last_server_restart"));
        Assert.Equal(1770000000123L, detailJson.Value<long>("observed_at_unix_ms"));

        var batchItem = UploadablePartyDetailBatchItem.FromDetail(detail, "tok-1");
        var batchJson = JObject.Parse(JsonConvert.SerializeObject(batchItem));
        Assert.Equal((ushort)74, batchJson.Value<ushort>("created_world"));
        Assert.Equal(1700000000U, batchJson.Value<uint>("last_server_restart"));
        Assert.Equal(1770000000123L, batchJson.Value<long>("observed_at_unix_ms"));
        Assert.Equal("tok-1", batchJson.Value<string>("capability_token"));
    }

    [Fact]
    public void Upload_listing_exposes_client_high_end_telemetry_fields() {
        Assert.Equal(typeof(bool), RequiredPropertyType<UploadableListing>("PfCategoryHighEnd"));
        Assert.Equal(typeof(bool), RequiredPropertyType<UploadableListing>("CfcExcelHighEnd"));
        Assert.Equal(typeof(bool), RequiredPropertyType<UploadableListing>("ClientHighEndDuty"));
    }

    [Fact]
    public void Upload_listing_serializes_client_high_end_telemetry_with_snake_case_names() {
        var listing = (UploadableListing)RuntimeHelpers.GetUninitializedObject(typeof(UploadableListing));
        SetBackingField(listing, "PfCategoryHighEnd", true);
        SetBackingField(listing, "CfcExcelHighEnd", false);
        SetBackingField(listing, "ClientHighEndDuty", true);

        var payload = JObject.Parse(JsonConvert.SerializeObject(listing));

        Assert.True(payload.Value<bool>("pf_category_high_end"));
        Assert.False(payload.Value<bool>("cfc_excel_high_end"));
        Assert.True(payload.Value<bool>("client_high_end_duty"));
    }

    [Theory]
    [InlineData(64u, true)]
    [InlineData(80u, true)]
    [InlineData(16u, false)]
    public void Upload_listing_detects_pf_category_high_end_flag(uint category, bool expected) {
        Assert.Equal(expected, InvokeHighEndCategoryDetection(category));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void Upload_listing_combines_client_high_end_signals(bool pfCategoryHighEnd, bool cfcExcelHighEnd, bool expected) {
        Assert.Equal(expected, InvokeClientHighEndDutyDetection(pfCategoryHighEnd, cfcExcelHighEnd));
    }

    [Fact]
    public void Upload_model_has_no_objective_normalization_helpers() {
        var normalizeObjectiveMethods = typeof(UploadableListing)
            .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(static method => method.Name.Contains("NormalizeObjective", StringComparison.Ordinal))
            .Select(static method => method.Name)
            .ToArray();

        Assert.Empty(normalizeObjectiveMethods);
    }

    [Fact]
    public void ParseJob_and_TomestoneJob_deserialize_server_manifest_metadata_in_snake_case() {
        const string json = """
        {
          "content_id": 424242,
          "name": "Manifest Tester",
          "server": "Tonberry",
          "region": "JP",
          "zone_id": 99,
          "difficulty_id": 101,
          "encounter_id": 8801,
          "secondary_encounter_id": 8802,
          "zone_encounters": [8800, 8801, 8802],
          "tomestone_expansion": "dawntrail",
          "tomestone_category": "raids",
          "tomestone_clears_category": "savage",
          "tomestone_zone_slug": "aac-future-savage",
          "tomestone_encounter_slug": "future-p1",
          "tomestone_encounter_names": ["future-p1", "future-p1-alt"],
          "tomestone_secondary_encounter_slug": "future-p2",
          "tomestone_secondary_encounter_names": ["future-p2"],
          "tomestone_progress_kind": "boss_percentage",
          "lease_token": "lease-manifest-1"
        }
        """;

        var parseJob = JsonConvert.DeserializeObject<ParseJob>(json);
        Assert.NotNull(parseJob);
        Assert.Equal([8800u, 8801u, 8802u], parseJob.ZoneEncounters);
        Assert.Equal("dawntrail", parseJob.TomestoneExpansion);
        Assert.Equal("raids", parseJob.TomestoneCategory);
        Assert.Equal("savage", parseJob.TomestoneClearsCategory);
        Assert.Equal("aac-future-savage", parseJob.TomestoneZoneSlug);
        Assert.Equal("future-p1", parseJob.TomestoneEncounterSlug);
        Assert.Equal(["future-p1", "future-p1-alt"], parseJob.TomestoneEncounterNames);
        Assert.Equal("future-p2", parseJob.TomestoneSecondaryEncounterSlug);
        Assert.Equal(["future-p2"], parseJob.TomestoneSecondaryEncounterNames);
        Assert.Equal("boss_percentage", parseJob.TomestoneProgressKind);

        var tomestoneJob = JsonConvert.DeserializeObject<TomestoneJob>(json);
        Assert.NotNull(tomestoneJob);
        Assert.Equal(parseJob.ZoneEncounters, tomestoneJob.ZoneEncounters);
        Assert.Equal(parseJob.TomestoneZoneSlug, tomestoneJob.TomestoneZoneSlug);
        Assert.Equal(parseJob.TomestoneSecondaryEncounterSlug, tomestoneJob.TomestoneSecondaryEncounterSlug);
        Assert.Equal(parseJob.TomestoneProgressKind, tomestoneJob.TomestoneProgressKind);
    }

    [Fact]
    public void ParseResult_serializes_fflogs_queried_encounters_in_snake_case() {
        var result = new ParseResult {
            ContentId = 777UL,
            ZoneId = 73,
            DifficultyId = 101,
            FflogsQueriedEncounters = [101, 102, 103, 104, 105],
            MatchedServer = "Tonberry",
            LeaseToken = "lease-777",
        };

        var json = JObject.Parse(JsonConvert.SerializeObject(result));
        Assert.Equal([101, 102, 103, 104, 105], json["fflogs_queried_encounters"]!.Values<int>().ToArray());
    }

    private static Type RequiredPropertyType<T>(string propertyName) {
        var property = typeof(T).GetProperty(propertyName);
        Assert.NotNull(property);
        return property.PropertyType;
    }

    private static void SetBackingField<T>(T instance, string propertyName, object value) {
        var field = typeof(T).GetField($"<{propertyName}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(instance, value);
    }

    private static bool InvokeHighEndCategoryDetection(uint category) {
        var categoryType = RequiredPropertyType<UploadableListing>("Category");
        var categoryValue = Enum.ToObject(categoryType, category);
        var method = typeof(UploadableListing).GetMethod(
            "IsPfCategoryHighEnd",
            BindingFlags.Static | BindingFlags.NonPublic
        );
        Assert.NotNull(method);
        return Assert.IsType<bool>(method.Invoke(null, [categoryValue]));
    }

    private static bool InvokeClientHighEndDutyDetection(bool pfCategoryHighEnd, bool cfcExcelHighEnd) {
        var method = typeof(UploadableListing).GetMethod(
            "IsClientHighEndDuty",
            BindingFlags.Static | BindingFlags.NonPublic
        );
        Assert.NotNull(method);
        return Assert.IsType<bool>(method.Invoke(null, [pfCategoryHighEnd, cfcExcelHighEnd]));
    }

    private static class DalamudAssemblyResolver {
        private static int _registered;

        internal static void Register() {
            if (Interlocked.Exchange(ref _registered, 1) != 0) {
                return;
            }

            AppDomain.CurrentDomain.AssemblyResolve += static (_, args) => {
                var assemblyName = new AssemblyName(args.Name).Name;
                if (string.IsNullOrWhiteSpace(assemblyName)) {
                    return null;
                }

                var dalamudHome = Environment.GetEnvironmentVariable("DALAMUD_HOME");
                if (string.IsNullOrWhiteSpace(dalamudHome)) {
                    dalamudHome = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "XIVLauncher",
                        "addon",
                        "Hooks",
                        "dev"
                    );
                }

                var candidatePath = Path.Combine(dalamudHome, assemblyName + ".dll");
                return File.Exists(candidatePath) ? Assembly.LoadFrom(candidatePath) : null;
            };
        }
    }
}
