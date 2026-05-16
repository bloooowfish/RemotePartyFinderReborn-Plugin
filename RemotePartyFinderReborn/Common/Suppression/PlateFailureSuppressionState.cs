using System;

#nullable enable

namespace RemotePartyFinderReborn;

internal sealed class PlateFailureSuppressionState {
    private static readonly TimeSpan DefaultGameLogSuppressionWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DefaultSelectOkSuppressionWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DefaultContentIdSuppressionWindow = TimeSpan.FromSeconds(1);
    private const int DefaultGameLogSuppressionBudget = 32;
    private const int DefaultSelectOkSuppressionBudget = 8;
    private const int DefaultUiOpenBudget = 2;
    private const int DefaultSelectOkDialogBudget = 2;

    private readonly ExpiringBudgetGate<ulong> _suppressedUiOpenBudgets = new();
    private readonly ExpiringBudgetGate<ulong> _suppressedSelectOkDialogBudgets = new();
    private readonly TimedWindowBudget _gameLogSuppression = new();
    private readonly TimedWindowBudget _selectOkSuppression = new();
    private readonly int _gameLogSuppressionBudget;
    private readonly int _selectOkSuppressionBudget;
    private readonly TimeSpan _gameLogSuppressionWindow;
    private readonly TimeSpan _selectOkSuppressionWindow;

    internal PlateFailureSuppressionState(
        int gameLogSuppressionBudget = DefaultGameLogSuppressionBudget,
        int selectOkSuppressionBudget = DefaultSelectOkSuppressionBudget,
        TimeSpan? gameLogSuppressionWindow = null,
        TimeSpan? selectOkSuppressionWindow = null
    ) {
        _gameLogSuppressionBudget = gameLogSuppressionBudget;
        _selectOkSuppressionBudget = selectOkSuppressionBudget;
        _gameLogSuppressionWindow = gameLogSuppressionWindow ?? DefaultGameLogSuppressionWindow;
        _selectOkSuppressionWindow = selectOkSuppressionWindow ?? DefaultSelectOkSuppressionWindow;
    }

    internal void TrackUiOpenBudget(ulong contentId, DateTime observedAtUtc, int budget = DefaultUiOpenBudget) {
        if (contentId == 0 || budget <= 0) {
            return;
        }

        _suppressedUiOpenBudgets.Arm(
            contentId,
            budget,
            observedAtUtc.Add(DefaultContentIdSuppressionWindow)
        );
    }

    internal void TrackSelectOkDialogBudget(ulong contentId, DateTime observedAtUtc, int budget = DefaultSelectOkDialogBudget) {
        if (contentId == 0 || budget <= 0) {
            return;
        }

        _suppressedSelectOkDialogBudgets.Arm(
            contentId,
            budget,
            observedAtUtc.Add(DefaultContentIdSuppressionWindow)
        );
    }

    internal void ArmDispatchedRequestUiSuppression(DateTime observedAtUtc) {
        _gameLogSuppression.Arm(
            _gameLogSuppressionBudget,
            observedAtUtc.Add(_gameLogSuppressionWindow)
        );
    }

    internal void ArmDispatchedRequestSuppression(DateTime observedAtUtc) {
        ArmDispatchedRequestUiSuppression(observedAtUtc);
        ArmSelectOkSuppression(observedAtUtc);
    }

    internal void ArmFailureSuppression(DateTime observedAtUtc) {
        ArmDispatchedRequestSuppression(observedAtUtc);
    }

    private void ArmSelectOkSuppression(DateTime observedAtUtc) {
        _selectOkSuppression.Arm(
            _selectOkSuppressionBudget,
            observedAtUtc.Add(_selectOkSuppressionWindow)
        );
    }

    internal bool TryConsumeSuppressedUiOpen(ulong contentId, DateTime observedAtUtc) {
        return _suppressedUiOpenBudgets.TryConsume(contentId, observedAtUtc);
    }

    internal bool TryConsumeSuppressedSelectOkDialog(ulong contentId, DateTime observedAtUtc) {
        return _suppressedSelectOkDialogBudgets.TryConsume(contentId, observedAtUtc);
    }

    internal SuppressionConsumption GetGameUiSuppression(DateTime observedAtUtc, bool hasInFlightRequest) {
        _ = hasInFlightRequest;
        return _gameLogSuppression.TryConsume(observedAtUtc);
    }

    internal SuppressionConsumption GetSelectOkSuppression(DateTime observedAtUtc, bool hasInFlightRequest) {
        _ = hasInFlightRequest;
        return _selectOkSuppression.TryConsume(observedAtUtc);
    }

    internal bool IsGameUiSuppressionWindowActive(DateTime observedAtUtc) {
        return _gameLogSuppression.IsActive(observedAtUtc);
    }
}
