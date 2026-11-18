using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class FFLogsActivationGateTests {
    static FFLogsActivationGateTests() {
        DalamudAssemblyResolver.Register();
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("client-id", "")]
    [InlineData("", "client-secret")]
    [InlineData("   ", "client-secret")]
    [InlineData("client-id", "   ")]
    public void Empty_or_whitespace_credentials_report_missing_and_locked(string clientId, string clientSecret) {
        var configuration = new Configuration {
            FFLogsClientId = clientId,
            FFLogsClientSecret = clientSecret,
            VerifiedFFLogsCredentialFingerprint = FFLogsActivationGate.ComputeCredentialFingerprint("other", "secret"),
        };
        var statusStore = new ApiCredentialStatusStore();
        using var gate = new FFLogsActivationGate(configuration, statusStore, static () => { });

        var snapshot = gate.GetSnapshot();

        Assert.Equal(PluginActivationState.MissingCredentials, snapshot.State);
        Assert.False(snapshot.IsOperational);
    }

    [Fact]
    public void Unverified_non_empty_credentials_remain_locked_before_oauth_check() {
        var configuration = new Configuration {
            FFLogsClientId = "client-id",
            FFLogsClientSecret = "client-secret",
        };
        var statusStore = new ApiCredentialStatusStore();
        using var gate = new FFLogsActivationGate(configuration, statusStore, static () => { });

        var snapshot = gate.GetSnapshot();

        Assert.Equal(PluginActivationState.MissingCredentials, snapshot.State);
        Assert.False(snapshot.IsOperational);
    }

    [Fact]
    public async Task Live_oauth_200_records_dpapi_protected_fingerprint_and_unlocks_plugin() {
        var configuration = new Configuration {
            FFLogsClientId = "  client-id  ",
            FFLogsClientSecret = "  client-secret  ",
            EnableFFLogsWorker = false,
        };
        var saveCount = 0;
        var statusStore = new ApiCredentialStatusStore();
        using var gate = new FFLogsActivationGate(configuration, statusStore, () => saveCount++);
        var handler = new RecordingHttpMessageHandler {
            OnSendAsync = static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent("{\"access_token\":\"tok\",\"expires_in\":3600}"),
            }),
        };
        using var client = new FFLogsClient(
            configuration,
            handler,
            credentialStatusChanged: gate.OnFFLogsCredentialStatusReported);

        var outcome = await gate.VerifyAsync(client.VerifyCredentialsAsync, CancellationToken.None);
        var snapshot = gate.GetSnapshot();

        Assert.True(outcome.Succeeded);
        Assert.True(outcome.Value);
        Assert.Equal(PluginActivationState.Active, snapshot.State);
        Assert.True(snapshot.IsOperational);
        Assert.True(configuration.EnableFFLogsWorker);
        Assert.Equal(1, saveCount);
        Assert.Equal(
            FFLogsActivationGate.ComputeCredentialFingerprint("client-id", "client-secret"),
            configuration.VerifiedFFLogsCredentialFingerprint);
        Assert.True(FFLogsActivationGate.HasMatchingVerifiedFingerprint(configuration));

        var fingerprintProp = typeof(Configuration).GetProperty(nameof(Configuration.VerifiedFFLogsCredentialFingerprint));
        Assert.NotNull(fingerprintProp);
        var converterAttr = fingerprintProp!.GetCustomAttributes<JsonConverterAttribute>().SingleOrDefault();
        Assert.NotNull(converterAttr);
        Assert.Equal(typeof(DpapiProtectedStringJsonConverter), converterAttr!.ConverterType);

        var json = JsonConvert.SerializeObject(configuration);
        Assert.DoesNotContain(configuration.VerifiedFFLogsCredentialFingerprint, json, StringComparison.Ordinal);
        Assert.Contains("dpapi:v1:", json, StringComparison.Ordinal);
        var roundTripped = JsonConvert.DeserializeObject<Configuration>(json)!;
        Assert.Equal(configuration.VerifiedFFLogsCredentialFingerprint, roundTripped.VerifiedFFLogsCredentialFingerprint);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task Live_oauth_rejection_locks_plugin_and_wipes_previous_verified_fingerprint(HttpStatusCode statusCode) {
        var existingFingerprint = FFLogsActivationGate.ComputeCredentialFingerprint("client-id", "old-secret");
        var configuration = new Configuration {
            FFLogsClientId = "client-id",
            FFLogsClientSecret = "old-secret",
            VerifiedFFLogsCredentialFingerprint = existingFingerprint,
        };
        var saveCount = 0;
        var statusStore = new ApiCredentialStatusStore();
        using var gate = new FFLogsActivationGate(configuration, statusStore, () => saveCount++);
        var handler = new RecordingHttpMessageHandler {
            OnSendAsync = (_, _) => Task.FromResult(new HttpResponseMessage(statusCode) {
                Content = new StringContent("invalid_client"),
            }),
        };
        using var client = new FFLogsClient(
            configuration,
            handler,
            credentialStatusChanged: gate.OnFFLogsCredentialStatusReported);

        var outcome = await gate.VerifyAsync(client.VerifyCredentialsAsync, CancellationToken.None);
        var snapshot = gate.GetSnapshot();

        Assert.False(outcome.Succeeded);
        Assert.False(outcome.TransientFailure);
        Assert.Equal(PluginActivationState.Rejected, snapshot.State);
        Assert.False(snapshot.IsOperational);
        Assert.Equal(string.Empty, configuration.VerifiedFFLogsCredentialFingerprint);
        Assert.Equal(1, saveCount);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    public async Task Transient_oauth_failure_keeps_previously_verified_credentials_active_but_keeps_unverified_locked(int statusCode) {
        var verifiedConfig = new Configuration {
            FFLogsClientId = "client-id",
            FFLogsClientSecret = "client-secret",
            VerifiedFFLogsCredentialFingerprint = FFLogsActivationGate.ComputeCredentialFingerprint("client-id", "client-secret"),
        };
        var verifiedStore = new ApiCredentialStatusStore();
        using var verifiedGate = new FFLogsActivationGate(verifiedConfig, verifiedStore, static () => { });
        var verifiedHandler = new RecordingHttpMessageHandler {
            OnSendAsync = (_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)statusCode)),
        };
        using var verifiedClient = new FFLogsClient(
            verifiedConfig,
            verifiedHandler,
            credentialStatusChanged: verifiedGate.OnFFLogsCredentialStatusReported);

        var verifiedOutcome = await verifiedGate.VerifyAsync(verifiedClient.VerifyCredentialsAsync, CancellationToken.None);
        var verifiedSnapshot = verifiedGate.GetSnapshot();

        Assert.False(verifiedOutcome.Succeeded);
        Assert.True(verifiedOutcome.TransientFailure);
        Assert.Equal(PluginActivationState.Active, verifiedSnapshot.State);
        Assert.True(verifiedSnapshot.IsOperational);

        var unverifiedConfig = new Configuration {
            FFLogsClientId = "client-id",
            FFLogsClientSecret = "client-secret",
        };
        var unverifiedStore = new ApiCredentialStatusStore();
        using var unverifiedGate = new FFLogsActivationGate(unverifiedConfig, unverifiedStore, static () => { });
        var unverifiedHandler = new RecordingHttpMessageHandler {
            OnSendAsync = (_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)statusCode)),
        };
        using var unverifiedClient = new FFLogsClient(
            unverifiedConfig,
            unverifiedHandler,
            credentialStatusChanged: unverifiedGate.OnFFLogsCredentialStatusReported);

        var unverifiedOutcome = await unverifiedGate.VerifyAsync(unverifiedClient.VerifyCredentialsAsync, CancellationToken.None);
        var unverifiedSnapshot = unverifiedGate.GetSnapshot();

        Assert.False(unverifiedOutcome.Succeeded);
        Assert.True(unverifiedOutcome.TransientFailure);
        Assert.Equal(PluginActivationState.VerificationFailed, unverifiedSnapshot.State);
        Assert.False(unverifiedSnapshot.IsOperational);
    }

    [Fact]
    public async Task In_flight_verification_reports_verifying_when_unverified_and_stays_active_when_already_verified() {
        var unverifiedConfig = new Configuration {
            FFLogsClientId = "client-id",
            FFLogsClientSecret = "client-secret",
        };
        using var unverifiedGate = new FFLogsActivationGate(unverifiedConfig, new ApiCredentialStatusStore(), static () => { });
        var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var unverifiedTask = unverifiedGate.VerifyAsync(async _ => {
            enteredFirst.SetResult(true);
            await releaseFirst.Task;
            unverifiedGate.OnFFLogsCredentialStatusReported(ApiCredentialValidationState.Accepted, "accepted");
            return OperationOutcome<bool>.Success(true);
        });

        await enteredFirst.Task;
        Assert.True(unverifiedGate.IsVerificationInFlight);
        var unverifiedInFlight = unverifiedGate.GetSnapshot();
        Assert.Equal(PluginActivationState.Verifying, unverifiedInFlight.State);
        Assert.False(unverifiedInFlight.IsOperational);

        releaseFirst.SetResult(true);
        await unverifiedTask;
        Assert.False(unverifiedGate.IsVerificationInFlight);
        Assert.True(unverifiedGate.GetSnapshot().IsOperational);

        var verifiedConfig = new Configuration {
            FFLogsClientId = "client-id",
            FFLogsClientSecret = "client-secret",
            VerifiedFFLogsCredentialFingerprint = FFLogsActivationGate.ComputeCredentialFingerprint("client-id", "client-secret"),
        };
        using var verifiedGate = new FFLogsActivationGate(verifiedConfig, new ApiCredentialStatusStore(), static () => { });
        var releaseSecond = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredSecond = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var verifiedTask = verifiedGate.VerifyAsync(async _ => {
            enteredSecond.SetResult(true);
            await releaseSecond.Task;
            verifiedGate.OnFFLogsCredentialStatusReported(ApiCredentialValidationState.Accepted, "accepted");
            return OperationOutcome<bool>.Success(true);
        });

        await enteredSecond.Task;
        Assert.True(verifiedGate.IsVerificationInFlight);
        var verifiedInFlight = verifiedGate.GetSnapshot();
        Assert.Equal(PluginActivationState.Active, verifiedInFlight.State);
        Assert.True(verifiedInFlight.IsOperational);

        releaseSecond.SetResult(true);
        await verifiedTask;
    }

    [Fact]
    public async Task Editing_client_id_or_secret_invalidates_match_immediately_until_reverified() {
        var configuration = new Configuration {
            FFLogsClientId = "client-id",
            FFLogsClientSecret = "client-secret",
        };
        var statusStore = new ApiCredentialStatusStore();
        using var gate = new FFLogsActivationGate(configuration, statusStore, static () => { });
        var handler = new RecordingHttpMessageHandler {
            OnSendAsync = static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent("{\"access_token\":\"tok\",\"expires_in\":3600}"),
            }),
        };
        using var client = new FFLogsClient(
            configuration,
            handler,
            credentialStatusChanged: gate.OnFFLogsCredentialStatusReported);

        await gate.VerifyAsync(client.VerifyCredentialsAsync, CancellationToken.None);
        Assert.True(gate.GetSnapshot().IsOperational);

        configuration.FFLogsClientSecret = "edited-secret";
        var afterSecretEdit = gate.GetSnapshot();
        Assert.False(afterSecretEdit.IsOperational);
        Assert.Equal(PluginActivationState.MissingCredentials, afterSecretEdit.State);

        await gate.VerifyAsync(client.VerifyCredentialsAsync, CancellationToken.None);
        Assert.True(gate.GetSnapshot().IsOperational);

        configuration.FFLogsClientId = "edited-id";
        var afterIdEdit = gate.GetSnapshot();
        Assert.False(afterIdEdit.IsOperational);
        Assert.Equal(PluginActivationState.MissingCredentials, afterIdEdit.State);
    }

    [Fact]
    public async Task ClearCredentialsAndLock_wipes_credentials_fingerprint_and_locks_immediately() {
        var configuration = new Configuration {
            FFLogsClientId = "client-id",
            FFLogsClientSecret = "client-secret",
        };
        var saveCount = 0;
        var statusStore = new ApiCredentialStatusStore();
        using var gate = new FFLogsActivationGate(configuration, statusStore, () => saveCount++);
        var handler = new RecordingHttpMessageHandler {
            OnSendAsync = static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent("{\"access_token\":\"tok\",\"expires_in\":3600}"),
            }),
        };
        using var client = new FFLogsClient(
            configuration,
            handler,
            credentialStatusChanged: gate.OnFFLogsCredentialStatusReported);

        await gate.VerifyAsync(client.VerifyCredentialsAsync, CancellationToken.None);
        Assert.True(gate.GetSnapshot().IsOperational);

        gate.ClearCredentialsAndLock();
        var snapshot = gate.GetSnapshot();

        Assert.Equal(string.Empty, configuration.FFLogsClientId);
        Assert.Equal(string.Empty, configuration.FFLogsClientSecret);
        Assert.Equal(string.Empty, configuration.VerifiedFFLogsCredentialFingerprint);
        Assert.Equal(ApiCredentialValidationState.NotChecked, statusStore.FFLogs.State);
        Assert.Equal(PluginActivationState.MissingCredentials, snapshot.State);
        Assert.False(snapshot.IsOperational);
        Assert.Equal(2, saveCount);
    }

    [Fact]
    public void PartyDetailCaptureRuntime_when_capture_disabled_performs_zero_captures_and_never_suppresses_addon_open() {
        var state = new PartyDetailCaptureState();
        var hookFactory = new CapturingHookFactory();
        var isCaptureEnabled = false;
        using var runtime = PartyDetailCaptureRuntime.CreateForTesting(
            state,
            hookFactory,
            warningSink: null,
            scannerHeadlessBackendKind: ScannerHeadlessBackendKind.NativeSuppression,
            isCaptureEnabled: () => isCaptureEnabled);

        var attemptId = Guid.NewGuid();
        runtime.ArmScannerRequest(attemptId, 9001UL, 44UL, allowHeadless: true);
        runtime.BeginScannerOpenAttempt(attemptId);

        Assert.True(hookFactory.OpenListing(0, 9001UL));
        Assert.True(hookFactory.OpenListingByContentId(0, 44UL));
        hookFactory.PopulateListingData(0, 0);
        var allowOriginalAddonOpen = hookFactory.OpenPfDetailAddon();

        runtime.EndScannerOpenAttempt(attemptId);

        var snapshot = new UploadablePartyDetail {
            ListingId = 9001U,
            LeaderContentId = 44UL,
            LeaderName = "Leader",
            HomeWorld = 77,
            CreatedWorld = 77,
            MemberContentIds = [44UL, 55UL],
            MemberJobs = [19, 24],
            SlotFlags = ["0x0000000000000000", "0x0000000000000000"],
        };
        runtime.TestFrameworkUpdate(() => (snapshot, null));
        runtime.TestFrameworkTick(snapshot);

        Assert.True(allowOriginalAddonOpen);
        Assert.False(state.HasActiveRequest);
        Assert.False(state.TryGetNextUnconsumedArrival(out _));
        Assert.False(runtime.ShouldSuppressScannerGameLog(0));
    }

    [Fact]
    public void Configuration_reset_helpers_restore_defaults_and_keep_default_upload_url() {
        var configuration = new Configuration {
            CircuitBreakerFailureThreshold = 10,
            CircuitBreakerBreakDurationMinutes = 15,
            DetailUploadRetryCount = 5,
            CharaCardResolveRetryCount = 4,
            FFLogsWorkerBaseDelayMs = 15000,
            FFLogsWorkerIdleDelayMs = 30000,
            FFLogsWorkerMaxBackoffDelayMs = 120000,
            FFLogsWorkerJitterMs = 5000,
            TomestoneRequestIntervalMs = 2500,
            TomestoneMaxConcurrency = 3,
            AutoDetailScanActionIntervalMs = 600,
            AutoDetailScanPostListingCooldownMs = 250,
            AutoDetailScanDetailTimeoutMs = 4500,
            AutoDetailScanRetryCount = 3,
        };

        configuration.ResetUploadBehaviorDefaults();
        configuration.ResetFFLogsWorkerTimingDefaults();
        configuration.ResetTomestoneAdvancedDefaults();
        configuration.ResetScannerDefaults();

        Assert.Equal(3, configuration.CircuitBreakerFailureThreshold);
        Assert.Equal(1, configuration.CircuitBreakerBreakDurationMinutes);
        Assert.Equal(1, configuration.DetailUploadRetryCount);
        Assert.Equal(1, configuration.CharaCardResolveRetryCount);

        Assert.Equal(5000, configuration.FFLogsWorkerBaseDelayMs);
        Assert.Equal(10000, configuration.FFLogsWorkerIdleDelayMs);
        Assert.Equal(60000, configuration.FFLogsWorkerMaxBackoffDelayMs);
        Assert.Equal(2000, configuration.FFLogsWorkerJitterMs);

        Assert.Equal(1000, configuration.TomestoneRequestIntervalMs);
        Assert.Equal(1, configuration.TomestoneMaxConcurrency);

        Assert.Equal(Configuration.DefaultScanActionIntervalMs, configuration.AutoDetailScanActionIntervalMs);
        Assert.Equal(Configuration.DefaultScanPostListingCooldownMs, configuration.AutoDetailScanPostListingCooldownMs);
        Assert.Equal(Configuration.DefaultScanDetailTimeoutMs, configuration.AutoDetailScanDetailTimeoutMs);
        Assert.Equal(Configuration.DefaultScanRetryCount, configuration.AutoDetailScanRetryCount);

        var defaultUrl = Assert.Single(Configuration.DefaultUploadUrls());
        Assert.Equal("http://127.0.0.1:8000", defaultUrl.Url);
        Assert.True(defaultUrl.IsDefault);
    }

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> OnSendAsync { get; set; }
            = static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => OnSendAsync(request, cancellationToken);
    }

    private sealed class CapturingHookFactory : IPartyDetailCaptureHookFactory, IDisposable {
        private Func<nint, ulong, bool>? _openListing;
        private Func<nint, ulong, bool>? _openListingByContentId;
        private Action<nint, nint>? _populateListingData;
        private Func<nint, uint, nint, nint, nint, nint, ushort, int, bool>? _pfDetailOpen;

        public IDisposable CreateHooks(
            Func<nint, ulong, bool> openListingDetour,
            Func<nint, ulong, bool> openListingByContentIdDetour,
            Action<nint, nint> populateListingDataDetour,
            Func<nint, uint, nint, nint, nint, nint, ushort, int, bool> pfDetailOpenDetour
        ) {
            _openListing = openListingDetour;
            _openListingByContentId = openListingByContentIdDetour;
            _populateListingData = populateListingDataDetour;
            _pfDetailOpen = pfDetailOpenDetour;
            return this;
        }

        public bool OpenListing(nint agent, ulong listingId) => _openListing!(agent, listingId);
        public bool OpenListingByContentId(nint agent, ulong contentId) => _openListingByContentId!(agent, contentId);
        public void PopulateListingData(nint agent, nint listingData) => _populateListingData!(agent, listingData);
        public bool OpenPfDetailAddon() => _pfDetailOpen!(0, 275U, 153, 0, 0, 2, 0, 4);

        public void Dispose() {
        }
    }
}
