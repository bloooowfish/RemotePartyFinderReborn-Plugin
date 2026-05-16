using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class UploadContractTests {
    static UploadContractTests() {
        DalamudAssemblyResolver.Register();
    }

    [Fact]
    public void Upload_models_keep_existing_uint_listing_id_contract() {
        Assert.Equal(typeof(uint), RequiredPropertyType<UploadableListing>(nameof(UploadableListing.Id)));
        Assert.Equal(typeof(uint), RequiredPropertyType<UploadablePartyDetail>(nameof(UploadablePartyDetail.ListingId)));
        Assert.Equal(typeof(uint), RequiredPropertyType<ListingDetailCapability>(nameof(ListingDetailCapability.ListingId)));
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
