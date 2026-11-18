#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace RemotePartyFinderReborn;

internal sealed class PlayerLocalDatabase : IDisposable {
    public const int MaxBatchSize = 100;

    private const int CurrentSchemaVersion = 3;
    private const int MaxCachedPlayers = 50_000;
    private const int MaxCachedIdentities = 50_000;
    private const int MaxCachedPartialIdentities = 20_000;
    private const int MaxAbsentIdentities = 4096;
    private const string PlayersTableName = "players";
    private const string IdentitiesTableName = "player_identities";
    private const string PartialIdentitiesTableName = "player_identity_partials";

    private static readonly TimeSpan CleanPlayerRetention = TimeSpan.FromDays(14);
    private static readonly TimeSpan CleanPruneInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan WriteCoalesceDelay = TimeSpan.FromMilliseconds(200);

    private readonly string _connectionString;
    private readonly object _stateLock = new();
    private readonly object _dbLock = new();
    private readonly SemaphoreSlim _flushSignal = new(0, int.MaxValue);
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Task _flushTask;

    private readonly Dictionary<ulong, CachedPlayerEntry> _players = new();
    private readonly Dictionary<ulong, CachedIdentityEntry> _identities = new();
    private readonly Dictionary<ulong, PartialCharacterIdentitySnapshot> _partialIdentities = new();
    private readonly HashSet<ulong> _absentIdentities = new();

    private Dictionary<ulong, CachedPlayerEntry> _pendingPlayerMutations = new();
    private Dictionary<ulong, DateTime> _pendingPlayerMarkUploadedOnly = new();
    private Dictionary<ulong, CachedIdentityEntry> _pendingIdentityUpserts = new();
    private Dictionary<ulong, DateTime> _pendingIdentitySubmittedOnly = new();
    private Dictionary<ulong, PartialCharacterIdentitySnapshot> _pendingPartialUpserts = new();
    private HashSet<ulong> _pendingPartialDeletes = new();

    private DateTime _lastCleanPruneUtc = DateTime.MinValue;
    private bool _pendingMarkAllDirty;
    private bool _pendingPruneCleanPlayers;
    private bool _playersMayBeIncomplete;
    private bool _identitiesMayBeIncomplete;
    private bool _partialsMayBeIncomplete;
    private int _dirtyPlayerCount;
    private bool _flushScheduled;
    private bool _disposing;
    private bool _disposed;

    public PlayerLocalDatabase(string databasePath) {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        _connectionString = $"Data Source={databasePath};Mode=ReadWriteCreate";

        var directoryPath = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directoryPath)) {
            Directory.CreateDirectory(directoryPath);
        }

        Initialize();
        _flushTask = Task.Run(FlushLoopAsync);
    }

    public int PendingCount {
        get {
            lock (_stateLock) {
                ThrowIfDisposedLocked();
                return _dirtyPlayerCount;
            }
        }
    }

    public void UpsertObservedPlayers(IEnumerable<UploadablePlayer> players) {
        ArgumentNullException.ThrowIfNull(players);

        var observedAtUtc = DateTime.UtcNow;
        var playerList = players as ICollection<UploadablePlayer> ?? players.ToList();
        if (playerList.Count == 0) {
            return;
        }

        lock (_stateLock) {
            ThrowIfDisposedLocked();

            foreach (var player in playerList) {
                CachedPlayerEntry nextEntry;
                if (_players.TryGetValue(player.ContentId, out var existing)) {
                    if (HasSameObservedPayload(existing.Player, player)) {
                        nextEntry = existing with {
                            LastSeenUtc = observedAtUtc,
                        };
                    } else {
                        var payloadHash = ComputePayloadHash(player);
                        var dirty = existing.Dirty || !string.Equals(existing.PayloadHash, payloadHash, StringComparison.Ordinal);
                        nextEntry = new CachedPlayerEntry(
                            ClonePlayer(player),
                            payloadHash,
                            dirty,
                            existing.FirstSeenUtc,
                            observedAtUtc,
                            existing.LastUploadedUtc
                        );
                    }
                } else {
                    var payloadHash = ComputePayloadHash(player);
                    nextEntry = new CachedPlayerEntry(
                        ClonePlayer(player),
                        payloadHash,
                        Dirty: true,
                        FirstSeenUtc: observedAtUtc,
                        LastSeenUtc: observedAtUtc,
                        LastUploadedUtc: null
                    );
                }

                SetPlayerCacheLocked(nextEntry);
                _pendingPlayerMutations[player.ContentId] = nextEntry;
                _pendingPlayerMarkUploadedOnly.Remove(player.ContentId);
            }

            if (_lastCleanPruneUtc == DateTime.MinValue || observedAtUtc - _lastCleanPruneUtc >= CleanPruneInterval) {
                _lastCleanPruneUtc = observedAtUtc;
                _pendingPruneCleanPlayers = true;
            }

            EnforcePlayerCacheLimitLocked(observedAtUtc);
            SignalFlushLocked();
        }
    }

    private static bool HasSameObservedPayload(UploadablePlayer existing, UploadablePlayer candidate) {
        return existing.HomeWorld == candidate.HomeWorld
               && existing.CurrentWorld == candidate.CurrentWorld
               && existing.AccountId == candidate.AccountId
               && string.Equals(existing.Name, candidate.Name, StringComparison.Ordinal);
    }

    public List<UploadablePlayer> TakePendingBatch(int maxBatchSize = MaxBatchSize) {
        var clampedLimit = Math.Clamp(maxBatchSize, 1, MaxBatchSize);
        List<UploadablePlayer> batch;
        bool shouldQueryOverflow;
        lock (_stateLock) {
            ThrowIfDisposedLocked();

            batch = _players.Values
                .Where(static entry => entry.Dirty)
                .OrderByDescending(static entry => entry.LastSeenUtc)
                .Take(clampedLimit)
                .Select(static entry => ClonePlayer(entry.Player))
                .ToList();
            shouldQueryOverflow = _playersMayBeIncomplete && batch.Count < clampedLimit;
        }

        if (!shouldQueryOverflow) {
            return batch;
        }

        var existingIds = batch.Select(static p => p.ContentId).ToHashSet();
        lock (_dbLock) {
            if (_disposed) {
                throw new ObjectDisposedException(nameof(PlayerLocalDatabase));
            }

            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT content_id, name, home_world, current_world, account_id
                FROM players
                WHERE dirty = 1
                ORDER BY last_seen_utc DESC
                LIMIT @limit;
                """;
            command.Parameters.AddWithValue("@limit", clampedLimit + existingIds.Count);

            using var reader = command.ExecuteReader();
            while (reader.Read() && batch.Count < clampedLimit) {
                var contentId = (ulong)reader.GetInt64(0);
                if (!existingIds.Add(contentId)) {
                    continue;
                }

                batch.Add(new UploadablePlayer {
                    ContentId = contentId,
                    Name = reader.GetString(1),
                    HomeWorld = Convert.ToUInt16(reader.GetInt64(2)),
                    CurrentWorld = Convert.ToUInt16(reader.GetInt64(3)),
                    AccountId = unchecked((ulong)reader.GetInt64(4)),
                });
            }
        }

        return batch;
    }

    public void MarkBatchUploaded(IEnumerable<UploadablePlayer> uploadedPlayers) {
        ArgumentNullException.ThrowIfNull(uploadedPlayers);

        var contentIds = uploadedPlayers
            .Select(static player => player.ContentId)
            .Distinct()
            .ToArray();

        if (contentIds.Length == 0) {
            return;
        }

        var uploadedAtUtc = DateTime.UtcNow;
        lock (_stateLock) {
            ThrowIfDisposedLocked();

            foreach (var contentId in contentIds) {
                if (_players.TryGetValue(contentId, out var existing)) {
                    var updated = existing with {
                        Dirty = false,
                        LastUploadedUtc = uploadedAtUtc,
                    };
                    SetPlayerCacheLocked(updated);
                    _pendingPlayerMutations[contentId] = updated;
                    _pendingPlayerMarkUploadedOnly.Remove(contentId);
                } else {
                    _pendingPlayerMutations.Remove(contentId);
                    _pendingPlayerMarkUploadedOnly[contentId] = uploadedAtUtc;
                }
            }

            SignalFlushLocked();
        }
    }

    public int MarkAllPlayersDirty() {
        int requeuedCount;
        bool queryTotalCount;
        lock (_stateLock) {
            ThrowIfDisposedLocked();

            foreach (var contentId in _players.Keys.ToArray()) {
                var existing = _players[contentId];
                if (!existing.Dirty) {
                    SetPlayerCacheLocked(existing with { Dirty = true });
                }
            }

            foreach (var contentId in _pendingPlayerMutations.Keys.ToArray()) {
                var pending = _pendingPlayerMutations[contentId];
                if (!pending.Dirty) {
                    _pendingPlayerMutations[contentId] = pending with { Dirty = true };
                }
            }

            _pendingPlayerMarkUploadedOnly.Clear();
            _pendingMarkAllDirty = true;
            requeuedCount = _players.Count;
            queryTotalCount = _playersMayBeIncomplete;
        }

        FlushPendingWrites();

        if (!queryTotalCount) {
            return requeuedCount;
        }

        lock (_dbLock) {
            if (_disposed) {
                throw new ObjectDisposedException(nameof(PlayerLocalDatabase));
            }

            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM players;";
            return Convert.ToInt32(command.ExecuteScalar());
        }
    }

    public bool TryGetIdentity(ulong contentId, [NotNullWhen(true)] out CharacterIdentitySnapshot? snapshot) {
        bool shouldQuerySqlite;
        lock (_stateLock) {
            ThrowIfDisposedLocked();
            if (_identities.TryGetValue(contentId, out var entry)) {
                snapshot = entry.Snapshot;
                return true;
            }

            shouldQuerySqlite = _identitiesMayBeIncomplete && !_absentIdentities.Contains(contentId);
        }

        if (!shouldQuerySqlite) {
            snapshot = null;
            return false;
        }

        lock (_dbLock) {
            if (_disposed) {
                throw new ObjectDisposedException(nameof(PlayerLocalDatabase));
            }

            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT name, home_world, world_name, last_resolved_utc, pending_upload, last_uploaded_utc
                FROM player_identities
                WHERE content_id = @content_id
                  AND name IS NOT NULL
                  AND home_world IS NOT NULL
                  AND world_name IS NOT NULL
                  AND last_resolved_utc IS NOT NULL
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("@content_id", (long)contentId);

            using var reader = command.ExecuteReader();
            if (!reader.Read()) {
                lock (_stateLock) {
                    if (!_disposed && !_identities.ContainsKey(contentId) && !_pendingIdentityUpserts.ContainsKey(contentId)) {
                        AddAbsentIdentityLocked(contentId);
                    }
                }
                snapshot = null;
                return false;
            }

            var loadedSnapshot = new CharacterIdentitySnapshot(
                contentId,
                reader.GetString(0),
                Convert.ToUInt16(reader.GetInt64(1)),
                reader.GetString(2),
                ParseTimestamp(reader.GetString(3))
            );
            var pendingUpload = reader.GetInt64(4) != 0;
            var lastUploadedUtc = reader.IsDBNull(5) ? (DateTime?)null : ParseTimestamp(reader.GetString(5));
            snapshot = loadedSnapshot;

            lock (_stateLock) {
                if (!_disposed) {
                    _absentIdentities.Remove(contentId);
                    if (!_identities.ContainsKey(contentId) && !_pendingIdentityUpserts.ContainsKey(contentId)) {
                        _identities[contentId] = new CachedIdentityEntry(loadedSnapshot, pendingUpload, lastUploadedUtc);
                        EnforceIdentityCacheLimitLocked();
                    }
                }
            }

            return true;
        }
    }

    public Dictionary<ulong, CharacterIdentitySnapshot> GetIdentities(IReadOnlyCollection<ulong> contentIds) {
        ArgumentNullException.ThrowIfNull(contentIds);

        var requestedIds = contentIds.Where(static id => id != 0).Distinct().ToArray();
        var identities = new Dictionary<ulong, CharacterIdentitySnapshot>(requestedIds.Length);
        if (requestedIds.Length == 0) {
            return identities;
        }

        List<ulong>? missingIds = null;
        lock (_stateLock) {
            ThrowIfDisposedLocked();

            foreach (var id in requestedIds) {
                if (_identities.TryGetValue(id, out var entry)) {
                    identities[id] = entry.Snapshot;
                } else if (_identitiesMayBeIncomplete && !_absentIdentities.Contains(id)) {
                    missingIds ??= new List<ulong>();
                    missingIds.Add(id);
                }
            }
        }

        if (missingIds is null || missingIds.Count == 0) {
            return identities;
        }

        const int MaxParametersPerQuery = 900;
        var loadedEntries = new List<CachedIdentityEntry>(missingIds.Count);
        lock (_dbLock) {
            if (_disposed) {
                throw new ObjectDisposedException(nameof(PlayerLocalDatabase));
            }

            using var connection = OpenConnection();
            for (var offset = 0; offset < missingIds.Count; offset += MaxParametersPerQuery) {
                var count = Math.Min(MaxParametersPerQuery, missingIds.Count - offset);
                using var command = connection.CreateCommand();
                var parameterNames = new string[count];
                for (var index = 0; index < count; index++) {
                    parameterNames[index] = $"@content_id_{index}";
                    command.Parameters.AddWithValue(parameterNames[index], (long)missingIds[offset + index]);
                }

                command.CommandText = $"""
                    SELECT content_id, name, home_world, world_name, last_resolved_utc, pending_upload, last_uploaded_utc
                    FROM player_identities
                    WHERE content_id IN ({string.Join(", ", parameterNames)})
                      AND name IS NOT NULL
                      AND home_world IS NOT NULL
                      AND world_name IS NOT NULL
                      AND last_resolved_utc IS NOT NULL;
                    """;

                using var reader = command.ExecuteReader();
                while (reader.Read()) {
                    var id = (ulong)reader.GetInt64(0);
                    var snapshot = new CharacterIdentitySnapshot(
                        id,
                        reader.GetString(1),
                        Convert.ToUInt16(reader.GetInt64(2)),
                        reader.GetString(3),
                        ParseTimestamp(reader.GetString(4))
                    );
                    var pendingUpload = reader.GetInt64(5) != 0;
                    var lastUploadedUtc = reader.IsDBNull(6) ? (DateTime?)null : ParseTimestamp(reader.GetString(6));
                    identities[id] = snapshot;
                    loadedEntries.Add(new CachedIdentityEntry(snapshot, pendingUpload, lastUploadedUtc));
                }
            }

            lock (_stateLock) {
                if (!_disposed) {
                    foreach (var loaded in loadedEntries) {
                        var id = loaded.Snapshot.ContentId;
                        _absentIdentities.Remove(id);
                        if (!_identities.ContainsKey(id) && !_pendingIdentityUpserts.ContainsKey(id)) {
                            _identities[id] = loaded;
                        }
                    }

                    foreach (var queriedId in missingIds) {
                        if (!identities.ContainsKey(queriedId)
                            && !_identities.ContainsKey(queriedId)
                            && !_pendingIdentityUpserts.ContainsKey(queriedId)) {
                            AddAbsentIdentityLocked(queriedId);
                        }
                    }

                    if (loadedEntries.Count > 0) {
                        EnforceIdentityCacheLimitLocked();
                    }
                }
            }
        }

        return identities;
    }

    public bool TryGetPartialIdentity(ulong contentId, [NotNullWhen(true)] out PartialCharacterIdentitySnapshot? snapshot) {
        bool shouldQuerySqlite;
        lock (_stateLock) {
            ThrowIfDisposedLocked();
            if (_partialIdentities.TryGetValue(contentId, out snapshot)) {
                return true;
            }

            shouldQuerySqlite = _partialsMayBeIncomplete && !_pendingPartialDeletes.Contains(contentId);
        }

        if (!shouldQuerySqlite) {
            snapshot = null;
            return false;
        }

        lock (_dbLock) {
            if (_disposed) {
                throw new ObjectDisposedException(nameof(PlayerLocalDatabase));
            }

            using var connection = OpenConnection();
            snapshot = ReadPartialIdentityFromConnection(connection, contentId);
            return snapshot is not null;
        }
    }

    private static PartialCharacterIdentitySnapshot? ReadPartialIdentityFromConnection(SqliteConnection connection, ulong contentId) {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name, home_world, world_name, last_updated_utc
            FROM player_identity_partials
            WHERE content_id = @content_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@content_id", (long)contentId);

        using var reader = command.ExecuteReader();
        if (!reader.Read()) {
            return null;
        }

        return new PartialCharacterIdentitySnapshot(
            contentId,
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : Convert.ToUInt16(reader.GetInt64(1)),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            ParseTimestamp(reader.GetString(3))
        );
    }

    public void UpsertPartialIdentityName(ulong contentId, string name, DateTime updatedAtUtc) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var trimmedName = name.Trim();
        lock (_stateLock) {
            ThrowIfDisposedLocked();

            if (_partialIdentities.TryGetValue(contentId, out var existing)) {
                ApplyPartialIdentityNameLocked(contentId, existing, trimmedName, updatedAtUtc);
                return;
            }

            if (!_partialsMayBeIncomplete || _pendingPartialDeletes.Contains(contentId)) {
                ApplyPartialIdentityNameLocked(contentId, existing: null, trimmedName, updatedAtUtc);
                return;
            }
        }

        lock (_dbLock) {
            if (_disposed) {
                throw new ObjectDisposedException(nameof(PlayerLocalDatabase));
            }

            PartialCharacterIdentitySnapshot? dbExisting;
            using (var connection = OpenConnection()) {
                dbExisting = ReadPartialIdentityFromConnection(connection, contentId);
            }

            lock (_stateLock) {
                ThrowIfDisposedLocked();
                var current = _partialIdentities.TryGetValue(contentId, out var memExisting)
                    ? memExisting
                    : (_pendingPartialDeletes.Contains(contentId) ? null : dbExisting);
                ApplyPartialIdentityNameLocked(contentId, current, trimmedName, updatedAtUtc);
            }
        }
    }

    private void ApplyPartialIdentityNameLocked(
        ulong contentId,
        PartialCharacterIdentitySnapshot? existing,
        string trimmedName,
        DateTime updatedAtUtc
    ) {
        var next = existing is not null
            ? existing with { Name = trimmedName, LastUpdatedAtUtc = updatedAtUtc }
            : new PartialCharacterIdentitySnapshot(contentId, trimmedName, null, null, updatedAtUtc);

        _partialIdentities[contentId] = next;
        _pendingPartialUpserts[contentId] = next;
        _pendingPartialDeletes.Remove(contentId);
        EnforcePartialCacheLimitLocked();
        SignalFlushLocked();
    }

    public void UpsertResolvedIdentity(CharacterIdentitySnapshot snapshot) {
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (_stateLock) {
            ThrowIfDisposedLocked();

            var entry = new CachedIdentityEntry(snapshot, PendingUpload: true, LastUploadedUtc: null);
            _identities[snapshot.ContentId] = entry;
            _pendingIdentityUpserts[snapshot.ContentId] = entry;
            _pendingIdentitySubmittedOnly.Remove(snapshot.ContentId);
            _absentIdentities.Clear();

            _partialIdentities.Remove(snapshot.ContentId);
            _pendingPartialUpserts.Remove(snapshot.ContentId);
            _pendingPartialDeletes.Add(snapshot.ContentId);

            EnforceIdentityCacheLimitLocked();
            SignalFlushLocked();
        }
    }

    public List<CharacterIdentitySnapshot> TakePendingIdentityUploads(int maxBatchSize) {
        var clampedLimit = Math.Clamp(maxBatchSize, 1, MaxBatchSize);
        List<CharacterIdentitySnapshot> pending;
        bool shouldQuerySqlite;

        lock (_stateLock) {
            ThrowIfDisposedLocked();

            pending = _identities.Values
                .Where(static entry => entry.PendingUpload)
                .OrderByDescending(static entry => entry.Snapshot.LastResolvedAtUtc)
                .ThenBy(static entry => entry.Snapshot.ContentId)
                .Take(clampedLimit)
                .Select(static entry => entry.Snapshot)
                .ToList();
            shouldQuerySqlite = _identitiesMayBeIncomplete;
        }

        if (!shouldQuerySqlite) {
            return pending;
        }

        lock (_dbLock) {
            if (_disposed) {
                throw new ObjectDisposedException(nameof(PlayerLocalDatabase));
            }

            HashSet<ulong> excludedIds;
            List<CharacterIdentitySnapshot> inMemoryPending;
            lock (_stateLock) {
                ThrowIfDisposedLocked();
                if (!_identitiesMayBeIncomplete) {
                    return _identities.Values
                        .Where(static entry => entry.PendingUpload)
                        .OrderByDescending(static entry => entry.Snapshot.LastResolvedAtUtc)
                        .ThenBy(static entry => entry.Snapshot.ContentId)
                        .Take(clampedLimit)
                        .Select(static entry => entry.Snapshot)
                        .ToList();
                }

                inMemoryPending = _identities.Values
                    .Where(static entry => entry.PendingUpload)
                    .Select(static entry => entry.Snapshot)
                    .ToList();
                excludedIds = new HashSet<ulong>(_identities.Count + _pendingIdentitySubmittedOnly.Count);
                foreach (var contentId in _identities.Keys) {
                    excludedIds.Add(contentId);
                }
                foreach (var contentId in _pendingIdentitySubmittedOnly.Keys) {
                    excludedIds.Add(contentId);
                }
            }

            var merged = new List<CharacterIdentitySnapshot>(inMemoryPending);
            var sqliteAdded = 0;
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT content_id, name, home_world, world_name, last_resolved_utc
                FROM player_identities
                WHERE pending_upload = 1
                  AND name IS NOT NULL
                  AND home_world IS NOT NULL
                  AND world_name IS NOT NULL
                  AND last_resolved_utc IS NOT NULL
                ORDER BY last_resolved_utc DESC, content_id ASC;
                """;

            using var reader = command.ExecuteReader();
            while (reader.Read() && sqliteAdded < clampedLimit) {
                var id = (ulong)reader.GetInt64(0);
                if (!excludedIds.Add(id)) {
                    continue;
                }

                merged.Add(new CharacterIdentitySnapshot(
                    id,
                    reader.GetString(1),
                    Convert.ToUInt16(reader.GetInt64(2)),
                    reader.GetString(3),
                    ParseTimestamp(reader.GetString(4))
                ));
                sqliteAdded++;
            }

            return merged
                .OrderByDescending(static snapshot => snapshot.LastResolvedAtUtc)
                .ThenBy(static snapshot => snapshot.ContentId)
                .Take(clampedLimit)
                .ToList();
        }
    }

    public void MarkIdentityUploadsSubmitted(IEnumerable<ulong> contentIds) {
        ArgumentNullException.ThrowIfNull(contentIds);

        var distinctContentIds = contentIds
            .Distinct()
            .ToArray();

        if (distinctContentIds.Length == 0) {
            return;
        }

        var uploadedAtUtc = DateTime.UtcNow;
        lock (_stateLock) {
            ThrowIfDisposedLocked();

            foreach (var contentId in distinctContentIds) {
                if (_identities.TryGetValue(contentId, out var existing)) {
                    var updated = existing with {
                        PendingUpload = false,
                        LastUploadedUtc = uploadedAtUtc,
                    };
                    _identities[contentId] = updated;
                    _pendingIdentityUpserts[contentId] = updated;
                    _pendingIdentitySubmittedOnly.Remove(contentId);
                } else {
                    _pendingIdentityUpserts.Remove(contentId);
                    _pendingIdentitySubmittedOnly[contentId] = uploadedAtUtc;
                }
            }

            SignalFlushLocked();
        }
    }

    internal void Flush() {
        FlushPendingWrites();
    }

    public void Dispose() {
        lock (_stateLock) {
            if (_disposed || _disposing) {
                return;
            }

            _disposing = true;
        }

        _disposeCts.Cancel();
        try {
            _flushTask.GetAwaiter().GetResult();
        } catch (OperationCanceledException) {
        } catch (Exception ex) {
            Plugin.Log?.Error($"PlayerLocalDatabase background flush failed during shutdown: {ex}");
        }

        try {
            FlushPendingWrites();
        } catch (Exception ex) {
            Plugin.Log?.Error($"PlayerLocalDatabase final flush failed during shutdown: {ex}");
        }

        lock (_stateLock) {
            _disposed = true;
        }

        _disposeCts.Dispose();
        _flushSignal.Dispose();

        try {
            SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
        } catch {
            // Best-effort pool cleanup on dispose.
        }
    }

    private void Initialize() {
        lock (_dbLock) {
            using var connection = OpenConnection();
            using (var pragmaCommand = connection.CreateCommand()) {
                pragmaCommand.CommandText = """
                    PRAGMA journal_mode = WAL;
                    PRAGMA synchronous = NORMAL;
                    PRAGMA busy_timeout = 3000;
                    """;
                pragmaCommand.ExecuteNonQuery();
            }

            EnsurePlayersTable(connection);
            MigrateIdentityColumns(connection);
            EnsurePartialIdentityTable(connection);
            EnsureSchemaVersion(connection);

            lock (_stateLock) {
                LoadInitialCachesLocked(connection);
            }
        }
    }

    private SqliteConnection OpenConnection() {
        if (_disposed) {
            throw new ObjectDisposedException(nameof(PlayerLocalDatabase));
        }

        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var timeoutCommand = connection.CreateCommand();
        timeoutCommand.CommandText = "PRAGMA busy_timeout = 3000;";
        timeoutCommand.ExecuteNonQuery();

        return connection;
    }

    private void LoadInitialCachesLocked(SqliteConnection connection) {
        _players.Clear();
        _identities.Clear();
        _partialIdentities.Clear();
        _dirtyPlayerCount = 0;
        _playersMayBeIncomplete = false;
        _identitiesMayBeIncomplete = false;
        _partialsMayBeIncomplete = false;

        using (var playersCommand = connection.CreateCommand()) {
            playersCommand.CommandText = """
                SELECT content_id, name, home_world, current_world, account_id, payload_hash, dirty, first_seen_utc, last_seen_utc, last_uploaded_utc
                FROM players
                ORDER BY dirty DESC, last_seen_utc DESC
                LIMIT @limit;
                """;
            playersCommand.Parameters.AddWithValue("@limit", MaxCachedPlayers + 1);

            using var reader = playersCommand.ExecuteReader();
            while (reader.Read()) {
                if (_players.Count >= MaxCachedPlayers) {
                    _playersMayBeIncomplete = true;
                    break;
                }

                var player = new UploadablePlayer {
                    ContentId = (ulong)reader.GetInt64(0),
                    Name = reader.GetString(1),
                    HomeWorld = Convert.ToUInt16(reader.GetInt64(2)),
                    CurrentWorld = Convert.ToUInt16(reader.GetInt64(3)),
                    AccountId = unchecked((ulong)reader.GetInt64(4)),
                };
                var entry = new CachedPlayerEntry(
                    player,
                    reader.GetString(5),
                    reader.GetInt64(6) != 0,
                    ParseTimestamp(reader.GetString(7)),
                    ParseTimestamp(reader.GetString(8)),
                    reader.IsDBNull(9) ? null : ParseTimestamp(reader.GetString(9))
                );
                SetPlayerCacheLocked(entry);
            }
        }

        using (var identitiesCommand = connection.CreateCommand()) {
            identitiesCommand.CommandText = """
                SELECT content_id, name, home_world, world_name, last_resolved_utc, pending_upload, last_uploaded_utc
                FROM player_identities
                WHERE name IS NOT NULL
                  AND home_world IS NOT NULL
                  AND world_name IS NOT NULL
                  AND last_resolved_utc IS NOT NULL
                ORDER BY pending_upload DESC, last_resolved_utc DESC
                LIMIT @limit;
                """;
            identitiesCommand.Parameters.AddWithValue("@limit", MaxCachedIdentities + 1);

            using var reader = identitiesCommand.ExecuteReader();
            while (reader.Read()) {
                if (_identities.Count >= MaxCachedIdentities) {
                    _identitiesMayBeIncomplete = true;
                    break;
                }

                var snapshot = new CharacterIdentitySnapshot(
                    (ulong)reader.GetInt64(0),
                    reader.GetString(1),
                    Convert.ToUInt16(reader.GetInt64(2)),
                    reader.GetString(3),
                    ParseTimestamp(reader.GetString(4))
                );
                var pendingUpload = reader.GetInt64(5) != 0;
                var lastUploadedUtc = reader.IsDBNull(6) ? (DateTime?)null : ParseTimestamp(reader.GetString(6));
                _identities[snapshot.ContentId] = new CachedIdentityEntry(snapshot, pendingUpload, lastUploadedUtc);
            }
        }

        using (var partialsCommand = connection.CreateCommand()) {
            partialsCommand.CommandText = """
                SELECT content_id, name, home_world, world_name, last_updated_utc
                FROM player_identity_partials
                ORDER BY last_updated_utc DESC
                LIMIT @limit;
                """;
            partialsCommand.Parameters.AddWithValue("@limit", MaxCachedPartialIdentities + 1);

            using var reader = partialsCommand.ExecuteReader();
            while (reader.Read()) {
                if (_partialIdentities.Count >= MaxCachedPartialIdentities) {
                    _partialsMayBeIncomplete = true;
                    break;
                }

                var partial = new PartialCharacterIdentitySnapshot(
                    (ulong)reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : Convert.ToUInt16(reader.GetInt64(2)),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    ParseTimestamp(reader.GetString(4))
                );
                _partialIdentities[partial.ContentId] = partial;
            }
        }
    }

    private async Task FlushLoopAsync() {
        while (!_disposeCts.IsCancellationRequested) {
            try {
                await _flushSignal.WaitAsync(_disposeCts.Token).ConfigureAwait(false);
                await Task.Delay(WriteCoalesceDelay, _disposeCts.Token).ConfigureAwait(false);
            } catch (OperationCanceledException) {
                break;
            }

            try {
                FlushPendingWrites();
            } catch (Exception ex) {
                Plugin.Log?.Error($"PlayerLocalDatabase async flush error: {ex}");
            }
        }
    }

    private void FlushPendingWrites() {
        lock (_dbLock) {
            PendingWriteBatch batch;
            lock (_stateLock) {
                _flushScheduled = false;
                if (!HasPendingWritesLocked()) {
                    return;
                }

                batch = CapturePendingBatchLocked();
            }

            try {
                WriteBatchToSqlite(batch);
            } catch {
                lock (_stateLock) {
                    RestorePendingBatchLocked(batch);
                }
                throw;
            }
        }
    }

    private bool HasPendingWritesLocked() {
        return _pendingMarkAllDirty
               || _pendingPruneCleanPlayers
               || _pendingPlayerMutations.Count > 0
               || _pendingPlayerMarkUploadedOnly.Count > 0
               || _pendingIdentityUpserts.Count > 0
               || _pendingIdentitySubmittedOnly.Count > 0
               || _pendingPartialUpserts.Count > 0
               || _pendingPartialDeletes.Count > 0;
    }

    private PendingWriteBatch CapturePendingBatchLocked() {
        var batch = new PendingWriteBatch(
            MarkAllDirty: _pendingMarkAllDirty,
            PruneCleanPlayers: _pendingPruneCleanPlayers,
            PlayerMutations: _pendingPlayerMutations,
            PlayerMarkUploadedOnly: _pendingPlayerMarkUploadedOnly,
            IdentityUpserts: _pendingIdentityUpserts,
            IdentitySubmittedOnly: _pendingIdentitySubmittedOnly,
            PartialUpserts: _pendingPartialUpserts,
            PartialDeletes: _pendingPartialDeletes
        );

        _pendingMarkAllDirty = false;
        _pendingPruneCleanPlayers = false;
        _pendingPlayerMutations = new Dictionary<ulong, CachedPlayerEntry>();
        _pendingPlayerMarkUploadedOnly = new Dictionary<ulong, DateTime>();
        _pendingIdentityUpserts = new Dictionary<ulong, CachedIdentityEntry>();
        _pendingIdentitySubmittedOnly = new Dictionary<ulong, DateTime>();
        _pendingPartialUpserts = new Dictionary<ulong, PartialCharacterIdentitySnapshot>();
        _pendingPartialDeletes = new HashSet<ulong>();

        return batch;
    }

    private void RestorePendingBatchLocked(PendingWriteBatch batch) {
        if (batch.MarkAllDirty) {
            _pendingMarkAllDirty = true;
            foreach (var contentId in _pendingPlayerMutations.Keys.ToArray()) {
                var current = _pendingPlayerMutations[contentId];
                if (!current.Dirty) {
                    _pendingPlayerMutations[contentId] = current with { Dirty = true };
                }
            }
        }

        _pendingPruneCleanPlayers |= batch.PruneCleanPlayers;

        foreach (var (id, entry) in batch.PlayerMutations) {
            if (!_pendingPlayerMutations.ContainsKey(id) && !_pendingPlayerMarkUploadedOnly.ContainsKey(id)) {
                _pendingPlayerMutations[id] = entry;
            }
        }

        foreach (var (id, uploadedAtUtc) in batch.PlayerMarkUploadedOnly) {
            if (!_pendingPlayerMutations.ContainsKey(id) && !_pendingPlayerMarkUploadedOnly.ContainsKey(id)) {
                _pendingPlayerMarkUploadedOnly[id] = uploadedAtUtc;
            }
        }

        foreach (var (id, entry) in batch.IdentityUpserts) {
            if (!_pendingIdentityUpserts.ContainsKey(id) && !_pendingIdentitySubmittedOnly.ContainsKey(id)) {
                _pendingIdentityUpserts[id] = entry;
            }
        }

        foreach (var (id, uploadedAtUtc) in batch.IdentitySubmittedOnly) {
            if (!_pendingIdentityUpserts.ContainsKey(id) && !_pendingIdentitySubmittedOnly.ContainsKey(id)) {
                _pendingIdentitySubmittedOnly[id] = uploadedAtUtc;
            }
        }

        foreach (var (id, partial) in batch.PartialUpserts) {
            if (!_pendingPartialUpserts.ContainsKey(id) && !_pendingPartialDeletes.Contains(id)) {
                _pendingPartialUpserts[id] = partial;
            }
        }

        foreach (var id in batch.PartialDeletes) {
            if (!_pendingPartialUpserts.ContainsKey(id)) {
                _pendingPartialDeletes.Add(id);
            }
        }
    }

    private void WriteBatchToSqlite(PendingWriteBatch batch) {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        if (batch.MarkAllDirty) {
            using var markDirtyCommand = connection.CreateCommand();
            markDirtyCommand.Transaction = transaction;
            markDirtyCommand.CommandText = "UPDATE players SET dirty = 1;";
            markDirtyCommand.ExecuteNonQuery();
        }

        if (batch.PlayerMutations.Count > 0) {
            using var upsertCommand = connection.CreateCommand();
            upsertCommand.Transaction = transaction;
            upsertCommand.CommandText = """
                INSERT INTO players (
                    content_id,
                    name,
                    home_world,
                    current_world,
                    account_id,
                    payload_hash,
                    dirty,
                    first_seen_utc,
                    last_seen_utc,
                    last_uploaded_utc
                )
                VALUES (
                    @content_id,
                    @name,
                    @home_world,
                    @current_world,
                    @account_id,
                    @payload_hash,
                    @dirty,
                    @first_seen_utc,
                    @last_seen_utc,
                    @last_uploaded_utc
                )
                ON CONFLICT(content_id) DO UPDATE SET
                    name = excluded.name,
                    home_world = excluded.home_world,
                    current_world = excluded.current_world,
                    account_id = excluded.account_id,
                    payload_hash = excluded.payload_hash,
                    dirty = excluded.dirty,
                    last_seen_utc = excluded.last_seen_utc,
                    last_uploaded_utc = excluded.last_uploaded_utc;
                """;

            var contentIdParam = upsertCommand.Parameters.Add("@content_id", SqliteType.Integer);
            var nameParam = upsertCommand.Parameters.Add("@name", SqliteType.Text);
            var homeWorldParam = upsertCommand.Parameters.Add("@home_world", SqliteType.Integer);
            var currentWorldParam = upsertCommand.Parameters.Add("@current_world", SqliteType.Integer);
            var accountIdParam = upsertCommand.Parameters.Add("@account_id", SqliteType.Integer);
            var payloadHashParam = upsertCommand.Parameters.Add("@payload_hash", SqliteType.Text);
            var dirtyParam = upsertCommand.Parameters.Add("@dirty", SqliteType.Integer);
            var firstSeenParam = upsertCommand.Parameters.Add("@first_seen_utc", SqliteType.Text);
            var lastSeenParam = upsertCommand.Parameters.Add("@last_seen_utc", SqliteType.Text);
            var lastUploadedParam = upsertCommand.Parameters.Add("@last_uploaded_utc", SqliteType.Text);

            foreach (var entry in batch.PlayerMutations.Values) {
                contentIdParam.Value = (long)entry.Player.ContentId;
                nameParam.Value = entry.Player.Name;
                homeWorldParam.Value = (long)entry.Player.HomeWorld;
                currentWorldParam.Value = (long)entry.Player.CurrentWorld;
                accountIdParam.Value = unchecked((long)entry.Player.AccountId);
                payloadHashParam.Value = entry.PayloadHash;
                dirtyParam.Value = entry.Dirty ? 1 : 0;
                firstSeenParam.Value = entry.FirstSeenUtc.ToString("O");
                lastSeenParam.Value = entry.LastSeenUtc.ToString("O");
                lastUploadedParam.Value = entry.LastUploadedUtc.HasValue
                    ? entry.LastUploadedUtc.Value.ToString("O")
                    : DBNull.Value;
                upsertCommand.ExecuteNonQuery();
            }
        }

        if (batch.PlayerMarkUploadedOnly.Count > 0) {
            using var markUploadedCommand = connection.CreateCommand();
            markUploadedCommand.Transaction = transaction;
            markUploadedCommand.CommandText = """
                UPDATE players
                SET dirty = 0,
                    last_uploaded_utc = @uploaded_at
                WHERE content_id = @content_id;
                """;

            var uploadedAtParam = markUploadedCommand.Parameters.Add("@uploaded_at", SqliteType.Text);
            var contentIdParam = markUploadedCommand.Parameters.Add("@content_id", SqliteType.Integer);

            foreach (var (contentId, uploadedAtUtc) in batch.PlayerMarkUploadedOnly) {
                uploadedAtParam.Value = uploadedAtUtc.ToString("O");
                contentIdParam.Value = (long)contentId;
                markUploadedCommand.ExecuteNonQuery();
            }
        }

        if (batch.IdentityUpserts.Count > 0) {
            using var identityCommand = connection.CreateCommand();
            identityCommand.Transaction = transaction;
            identityCommand.CommandText = """
                INSERT INTO player_identities (
                    content_id,
                    name,
                    home_world,
                    world_name,
                    last_resolved_utc,
                    pending_upload,
                    last_uploaded_utc
                )
                VALUES (
                    @content_id,
                    @name,
                    @home_world,
                    @world_name,
                    @last_resolved_utc,
                    @pending_upload,
                    @last_uploaded_utc
                )
                ON CONFLICT(content_id) DO UPDATE SET
                    name = excluded.name,
                    home_world = excluded.home_world,
                    world_name = excluded.world_name,
                    last_resolved_utc = excluded.last_resolved_utc,
                    pending_upload = excluded.pending_upload,
                    last_uploaded_utc = excluded.last_uploaded_utc;
                """;

            var contentIdParam = identityCommand.Parameters.Add("@content_id", SqliteType.Integer);
            var nameParam = identityCommand.Parameters.Add("@name", SqliteType.Text);
            var homeWorldParam = identityCommand.Parameters.Add("@home_world", SqliteType.Integer);
            var worldNameParam = identityCommand.Parameters.Add("@world_name", SqliteType.Text);
            var lastResolvedParam = identityCommand.Parameters.Add("@last_resolved_utc", SqliteType.Text);
            var pendingUploadParam = identityCommand.Parameters.Add("@pending_upload", SqliteType.Integer);
            var lastUploadedParam = identityCommand.Parameters.Add("@last_uploaded_utc", SqliteType.Text);

            foreach (var entry in batch.IdentityUpserts.Values) {
                contentIdParam.Value = (long)entry.Snapshot.ContentId;
                nameParam.Value = entry.Snapshot.Name;
                homeWorldParam.Value = (long)entry.Snapshot.HomeWorld;
                worldNameParam.Value = entry.Snapshot.WorldName;
                lastResolvedParam.Value = entry.Snapshot.LastResolvedAtUtc.ToString("O");
                pendingUploadParam.Value = entry.PendingUpload ? 1 : 0;
                lastUploadedParam.Value = entry.LastUploadedUtc.HasValue
                    ? entry.LastUploadedUtc.Value.ToString("O")
                    : DBNull.Value;
                identityCommand.ExecuteNonQuery();
            }
        }

        if (batch.IdentitySubmittedOnly.Count > 0) {
            using var submittedCommand = connection.CreateCommand();
            submittedCommand.Transaction = transaction;
            submittedCommand.CommandText = """
                UPDATE player_identities
                SET pending_upload = 0,
                    last_uploaded_utc = @uploaded_at
                WHERE content_id = @content_id;
                """;

            var uploadedAtParam = submittedCommand.Parameters.Add("@uploaded_at", SqliteType.Text);
            var contentIdParam = submittedCommand.Parameters.Add("@content_id", SqliteType.Integer);

            foreach (var (contentId, uploadedAtUtc) in batch.IdentitySubmittedOnly) {
                uploadedAtParam.Value = uploadedAtUtc.ToString("O");
                contentIdParam.Value = (long)contentId;
                submittedCommand.ExecuteNonQuery();
            }
        }

        if (batch.PartialUpserts.Count > 0) {
            using var partialCommand = connection.CreateCommand();
            partialCommand.Transaction = transaction;
            partialCommand.CommandText = """
                INSERT INTO player_identity_partials (
                    content_id,
                    name,
                    home_world,
                    world_name,
                    last_updated_utc
                )
                VALUES (
                    @content_id,
                    @name,
                    @home_world,
                    @world_name,
                    @last_updated_utc
                )
                ON CONFLICT(content_id) DO UPDATE SET
                    name = excluded.name,
                    last_updated_utc = excluded.last_updated_utc;
                """;

            var contentIdParam = partialCommand.Parameters.Add("@content_id", SqliteType.Integer);
            var nameParam = partialCommand.Parameters.Add("@name", SqliteType.Text);
            var homeWorldParam = partialCommand.Parameters.Add("@home_world", SqliteType.Integer);
            var worldNameParam = partialCommand.Parameters.Add("@world_name", SqliteType.Text);
            var lastUpdatedParam = partialCommand.Parameters.Add("@last_updated_utc", SqliteType.Text);

            foreach (var partial in batch.PartialUpserts.Values) {
                contentIdParam.Value = (long)partial.ContentId;
                nameParam.Value = partial.Name is null ? DBNull.Value : partial.Name;
                homeWorldParam.Value = partial.HomeWorld.HasValue ? (long)partial.HomeWorld.Value : DBNull.Value;
                worldNameParam.Value = partial.WorldName is null ? DBNull.Value : partial.WorldName;
                lastUpdatedParam.Value = partial.LastUpdatedAtUtc.ToString("O");
                partialCommand.ExecuteNonQuery();
            }
        }

        if (batch.PartialDeletes.Count > 0) {
            using var deletePartialCommand = connection.CreateCommand();
            deletePartialCommand.Transaction = transaction;
            deletePartialCommand.CommandText = """
                DELETE FROM player_identity_partials
                WHERE content_id = @content_id;
                """;
            var contentIdParam = deletePartialCommand.Parameters.Add("@content_id", SqliteType.Integer);
            foreach (var contentId in batch.PartialDeletes) {
                contentIdParam.Value = (long)contentId;
                deletePartialCommand.ExecuteNonQuery();
            }
        }

        if (batch.PruneCleanPlayers) {
            PruneCleanRows(connection, transaction);
        }

        if (batch.PartialUpserts.Count > 0) {
            PruneOverflowPartials(connection, transaction);
        }

        transaction.Commit();
    }

    private void SignalFlushLocked() {
        if (_disposing || _disposed || _flushScheduled) {
            return;
        }

        _flushScheduled = true;
        _flushSignal.Release();
    }

    private void ThrowIfDisposedLocked() {
        if (_disposed) {
            throw new ObjectDisposedException(nameof(PlayerLocalDatabase));
        }
    }

    private void SetPlayerCacheLocked(CachedPlayerEntry nextEntry) {
        if (_players.TryGetValue(nextEntry.Player.ContentId, out var previous)) {
            if (previous.Dirty != nextEntry.Dirty) {
                _dirtyPlayerCount += nextEntry.Dirty ? 1 : -1;
            }
        } else if (nextEntry.Dirty) {
            _dirtyPlayerCount++;
        }

        _players[nextEntry.Player.ContentId] = nextEntry;
    }

    private void RemovePlayerCacheLocked(ulong contentId) {
        if (_players.Remove(contentId, out var removed)) {
            if (removed.Dirty) {
                _dirtyPlayerCount--;
            }

            _playersMayBeIncomplete = true;
        }
    }

    private void EnforcePlayerCacheLimitLocked(DateTime nowUtc) {
        var cutoff = nowUtc - CleanPlayerRetention;
        List<ulong>? expiredIds = null;
        foreach (var (contentId, entry) in _players) {
            if (!entry.Dirty && entry.LastSeenUtc < cutoff) {
                expiredIds ??= new List<ulong>();
                expiredIds.Add(contentId);
            }
        }

        if (expiredIds is not null) {
            foreach (var contentId in expiredIds) {
                RemovePlayerCacheLocked(contentId);
            }
        }

        if (_players.Count <= MaxCachedPlayers) {
            return;
        }

        var overflow = _players.Count - MaxCachedPlayers;
        var evictionCandidates = _players.Values
            .OrderBy(static entry => entry.Dirty)
            .ThenBy(static entry => entry.LastSeenUtc)
            .Take(overflow)
            .Select(static entry => entry.Player.ContentId)
            .ToArray();

        foreach (var contentId in evictionCandidates) {
            RemovePlayerCacheLocked(contentId);
        }
    }

    private void EnforceIdentityCacheLimitLocked() {
        if (_identities.Count <= MaxCachedIdentities) {
            return;
        }

        var overflow = _identities.Count - MaxCachedIdentities;
        var evictionCandidates = _identities.Values
            .OrderBy(static entry => entry.PendingUpload)
            .ThenBy(static entry => entry.Snapshot.LastResolvedAtUtc)
            .Take(overflow)
            .Select(static entry => entry.Snapshot.ContentId)
            .ToArray();

        foreach (var contentId in evictionCandidates) {
            if (_identities.Remove(contentId)) {
                _identitiesMayBeIncomplete = true;
            }
        }
    }

    private void AddAbsentIdentityLocked(ulong contentId) {
        if (_absentIdentities.Count >= MaxAbsentIdentities) {
            _absentIdentities.Clear();
        }

        _absentIdentities.Add(contentId);
    }

    internal void EvictIdentityFromMemoryForTesting(ulong contentId) {
        lock (_stateLock) {
            ThrowIfDisposedLocked();
            _identities.Remove(contentId);
            _identitiesMayBeIncomplete = true;
        }
    }

    internal void EvictPartialFromMemoryForTesting(ulong contentId) {
        lock (_stateLock) {
            ThrowIfDisposedLocked();
            _partialIdentities.Remove(contentId);
            _partialsMayBeIncomplete = true;
        }
    }

    internal bool IsIdentityCachedInMemoryForTesting(ulong contentId) {
        lock (_stateLock) {
            return _identities.ContainsKey(contentId);
        }
    }

    internal bool IsIdentityNegativeCachedForTesting(ulong contentId) {
        lock (_stateLock) {
            return _absentIdentities.Contains(contentId);
        }
    }

    internal bool HasPendingCleanPruneForTesting() {
        lock (_stateLock) {
            return _pendingPruneCleanPlayers;
        }
    }

    private void EnforcePartialCacheLimitLocked() {
        if (_partialIdentities.Count <= MaxCachedPartialIdentities) {
            return;
        }

        var overflow = _partialIdentities.Count - MaxCachedPartialIdentities;
        var evictionCandidates = _partialIdentities.Values
            .OrderBy(static entry => entry.LastUpdatedAtUtc)
            .Take(overflow)
            .Select(static entry => entry.ContentId)
            .ToArray();

        foreach (var contentId in evictionCandidates) {
            if (_partialIdentities.Remove(contentId)) {
                _partialsMayBeIncomplete = true;
            }
        }
    }

    private static UploadablePlayer ClonePlayer(UploadablePlayer player) {
        return new UploadablePlayer {
            ContentId = player.ContentId,
            Name = player.Name,
            HomeWorld = player.HomeWorld,
            CurrentWorld = player.CurrentWorld,
            AccountId = player.AccountId,
        };
    }

    private static void EnsurePlayersTable(SqliteConnection connection) {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS players (
                content_id INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                home_world INTEGER NOT NULL,
                current_world INTEGER NOT NULL,
                account_id INTEGER NOT NULL,
                payload_hash TEXT NOT NULL,
                dirty INTEGER NOT NULL DEFAULT 1,
                first_seen_utc TEXT NOT NULL,
                last_seen_utc TEXT NOT NULL,
                last_uploaded_utc TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_players_dirty_last_seen ON players(dirty, last_seen_utc);
            """;
        command.ExecuteNonQuery();
    }

    private static void MigrateIdentityColumns(SqliteConnection connection) {
        if (!TableExists(connection, IdentitiesTableName)) {
            using var createTableCommand = connection.CreateCommand();
            createTableCommand.CommandText = """
                CREATE TABLE player_identities (
                    content_id INTEGER PRIMARY KEY,
                    name TEXT NOT NULL,
                    home_world INTEGER NOT NULL,
                    world_name TEXT NOT NULL,
                    last_resolved_utc TEXT NOT NULL,
                    pending_upload INTEGER NOT NULL DEFAULT 0,
                    last_uploaded_utc TEXT
                );
                """;
            createTableCommand.ExecuteNonQuery();
        } else {
            var existingColumns = GetColumnNames(connection, IdentitiesTableName);

            AddColumnIfMissing(connection, existingColumns, IdentitiesTableName, "name", "TEXT");
            AddColumnIfMissing(connection, existingColumns, IdentitiesTableName, "home_world", "INTEGER");
            AddColumnIfMissing(connection, existingColumns, IdentitiesTableName, "world_name", "TEXT");
            AddColumnIfMissing(connection, existingColumns, IdentitiesTableName, "last_resolved_utc", "TEXT");
            AddColumnIfMissing(connection, existingColumns, IdentitiesTableName, "pending_upload", "INTEGER NOT NULL DEFAULT 0");
            AddColumnIfMissing(connection, existingColumns, IdentitiesTableName, "last_uploaded_utc", "TEXT");
        }

        using var createIndexCommand = connection.CreateCommand();
        createIndexCommand.CommandText = """
            CREATE INDEX IF NOT EXISTS idx_player_identities_pending_upload_last_resolved
            ON player_identities(pending_upload, last_resolved_utc);
            """;
        createIndexCommand.ExecuteNonQuery();
    }

    private static void EnsurePartialIdentityTable(SqliteConnection connection) {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS player_identity_partials (
                content_id INTEGER PRIMARY KEY,
                name TEXT,
                home_world INTEGER,
                world_name TEXT,
                last_updated_utc TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    private static void EnsureSchemaVersion(SqliteConnection connection) {
        using var getVersionCommand = connection.CreateCommand();
        getVersionCommand.CommandText = "PRAGMA user_version;";
        var currentVersion = Convert.ToInt32(getVersionCommand.ExecuteScalar() ?? 0);

        if (currentVersion >= CurrentSchemaVersion) {
            return;
        }

        using var setVersionCommand = connection.CreateCommand();
        setVersionCommand.CommandText = $"PRAGMA user_version = {CurrentSchemaVersion};";
        setVersionCommand.ExecuteNonQuery();
    }

    private static bool TableExists(SqliteConnection connection, string tableName) {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'table' AND name = @table_name;
            """;
        command.Parameters.AddWithValue("@table_name", tableName);
        var result = command.ExecuteScalar();
        return result is long count && count > 0;
    }

    private static HashSet<string> GetColumnNames(SqliteConnection connection, string tableName) {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read()) {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static void AddColumnIfMissing(
        SqliteConnection connection,
        ISet<string> existingColumns,
        string tableName,
        string columnName,
        string columnDefinition
    ) {
        if (existingColumns.Contains(columnName)) {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};";
        command.ExecuteNonQuery();
        existingColumns.Add(columnName);
    }

    private static string ComputePayloadHash(UploadablePlayer player) {
        var canonical = string.Concat(
            player.ContentId,
            "|",
            player.Name,
            "|",
            player.HomeWorld,
            "|",
            player.CurrentWorld,
            "|",
            player.AccountId
        );

        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToBase64String(hash);
    }

    private static DateTime ParseTimestamp(string value) {
        return DateTime.Parse(
            value,
            provider: null,
            System.Globalization.DateTimeStyles.RoundtripKind
        );
    }

    private static void PruneCleanRows(SqliteConnection connection, SqliteTransaction transaction) {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM players
            WHERE dirty = 0
              AND last_seen_utc < @cutoff;
            """;
        command.Parameters.AddWithValue("@cutoff", DateTime.UtcNow.AddDays(-14).ToString("O"));
        command.ExecuteNonQuery();
    }

    private static void PruneOverflowPartials(SqliteConnection connection, SqliteTransaction transaction) {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM player_identity_partials
            WHERE content_id NOT IN (
                SELECT content_id
                FROM player_identity_partials
                ORDER BY last_updated_utc DESC
                LIMIT @limit
            );
            """;
        command.Parameters.AddWithValue("@limit", MaxCachedPartialIdentities);
        command.ExecuteNonQuery();
    }

    private sealed record CachedPlayerEntry(
        UploadablePlayer Player,
        string PayloadHash,
        bool Dirty,
        DateTime FirstSeenUtc,
        DateTime LastSeenUtc,
        DateTime? LastUploadedUtc
    );

    private sealed record CachedIdentityEntry(
        CharacterIdentitySnapshot Snapshot,
        bool PendingUpload,
        DateTime? LastUploadedUtc
    );

    private sealed record PendingWriteBatch(
        bool MarkAllDirty,
        bool PruneCleanPlayers,
        Dictionary<ulong, CachedPlayerEntry> PlayerMutations,
        Dictionary<ulong, DateTime> PlayerMarkUploadedOnly,
        Dictionary<ulong, CachedIdentityEntry> IdentityUpserts,
        Dictionary<ulong, DateTime> IdentitySubmittedOnly,
        Dictionary<ulong, PartialCharacterIdentitySnapshot> PartialUpserts,
        HashSet<ulong> PartialDeletes
    );
}

internal sealed class UploadablePlayer {
    public ulong ContentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public ushort HomeWorld { get; set; }
    public ushort CurrentWorld { get; set; }
    public ulong AccountId { get; set; }
}
