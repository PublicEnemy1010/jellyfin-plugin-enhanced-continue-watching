using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ContinueWatching.Infrastructure.Repositories;

/// <summary>
/// The authoritative play-count ledger.
/// <para>
/// Jellyfin's own <c>UserItemData.PlayCount</c> cannot be trusted to accumulate: marking an item
/// unwatched zeroes it (and nulls <c>LastPlayedDate</c>), and marking an item watched is
/// idempotent, so it never climbs past 1 from the UI. This store keeps the real count per
/// (user, item) and <see cref="Services.PlayCountService.PlayCountService"/> writes it back over
/// whatever Jellyfin persisted.
/// </para>
/// <para>
/// Persistence mirrors <see cref="CursorStore"/>: in-memory, flushed to disk every 30s and on a
/// graceful shutdown, written via a temp file and an atomic rename.
/// </para>
/// </summary>
public sealed class PlayCountStore : IHostedService, IDisposable
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly Dictionary<PlayCountKey, PlayCountDto> _counts = [];
    private readonly ReaderWriterLockSlim _lock = new();
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private readonly ILogger<PlayCountStore> _logger;
    private readonly string _filePath;
    private CancellationTokenSource? _stoppingTokenSource;
    private Task? _flushTask;
    private int _dirty;

    public PlayCountStore(IApplicationPaths applicationPaths, ILogger<PlayCountStore> logger)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);
        _logger = logger;

        var directoryPath = Path.Combine(applicationPaths.PluginConfigurationsPath, "Jellyfin.Plugin.ContinueWatching");

        Directory.CreateDirectory(directoryPath);

        _filePath = Path.Combine(directoryPath, "playcounts.json");
    }

    public PlayCountDto? TryGet(PlayCountKey key)
    {
        _lock.EnterReadLock();
        try
        {
            return _counts.GetValueOrDefault(key);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Raises the stored count to <paramref name="count"/> and returns the value now held.
    /// The ledger is a high-water mark, so a lower value is ignored -- that is what makes a
    /// count survive an unwatch. Use <see cref="Set"/> when the caller is authoritative.
    /// </summary>
    public int RaiseTo(PlayCountKey key, int count, DateTimeOffset? lastCountedUtc)
    {
        _lock.EnterWriteLock();
        try
        {
            PlayCountDto? existing = _counts.GetValueOrDefault(key);
            if (existing is not null && existing.Count >= count)
            {
                return existing.Count;
            }

            // An item nobody has played has nothing to protect; storing a zero would only
            // grow the ledger with entries that can never change an outcome.
            if (existing is null && count <= 0)
            {
                return 0;
            }

            _counts[key] = new PlayCountDto(
                key.UserId,
                key.ItemId,
                count,
                lastCountedUtc ?? existing?.LastCountedUtc);
            Interlocked.Exchange(ref _dirty, 1);
            return count;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public void Set(PlayCountKey key, int count, DateTimeOffset? lastCountedUtc)
    {
        _lock.EnterWriteLock();
        try
        {
            _counts[key] = new PlayCountDto(key.UserId, key.ItemId, count, lastCountedUtc);
            Interlocked.Exchange(ref _dirty, 1);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Adds one to the stored count unless the previous increment landed inside
    /// <paramref name="dedupeWindow"/>. Returns the count now held, and whether it moved.
    /// </summary>
    public (int Count, bool Incremented) Increment(
        PlayCountKey key,
        DateTimeOffset now,
        TimeSpan dedupeWindow)
    {
        _lock.EnterWriteLock();
        try
        {
            PlayCountDto? existing = _counts.GetValueOrDefault(key);

            if (existing?.LastCountedUtc is { } lastCounted && now - lastCounted < dedupeWindow)
            {
                return (existing.Count, false);
            }

            int next = (existing?.Count ?? 0) + 1;
            _counts[key] = new PlayCountDto(key.UserId, key.ItemId, next, now);
            Interlocked.Exchange(ref _dirty, 1);
            return (next, true);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stoppingTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _flushTask = FlushPeriodically(_stoppingTokenSource.Token);
        return Load();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_stoppingTokenSource is not null)
        {
            await _stoppingTokenSource.CancelAsync().ConfigureAwait(false);
        }

        if (_flushTask is not null)
        {
            try
            {
                await _flushTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during host shutdown.
            }
        }

        // Deliberately not the host's token: a slow plugin elsewhere can exhaust the shutdown
        // timeout before this runs, and a cancelled final flush loses everything since the last
        // periodic one. Writing one small local file is worth finishing.
        await Flush(CancellationToken.None, force: true).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _stoppingTokenSource?.Dispose();
        _lock.Dispose();
        _flushLock.Dispose();
    }

    private async Task Load()
    {
        if (!File.Exists(_filePath))
        {
            return;
        }

        try
        {
            await using FileStream stream = File.OpenRead(_filePath);
            List<PlayCountDto>? counts = await JsonSerializer.DeserializeAsync<List<PlayCountDto>>(stream, JsonOptions);

            if (counts is null)
            {
                return;
            }

            foreach (PlayCountDto count in counts)
            {
                _counts[new PlayCountKey(count.UserId, count.ItemId)] = count;
            }

            _logger.LogInformation("Loaded {Count} play-count ledger entries from {FilePath}", _counts.Count, _filePath);
        }
        catch (IOException exception)
        {
            _logger.LogError(exception, "Failed to load the play-count ledger from {FilePath}", _filePath);
        }
        catch (JsonException exception)
        {
            _logger.LogError(exception, "The play-count ledger at {FilePath} is not valid JSON", _filePath);
        }
    }

    private async Task FlushPeriodically(CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(FlushInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await Flush(cancellationToken, force: false).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogError(
                        exception,
                        "Failed to flush the play-count ledger to {FilePath}; the next interval will retry",
                        _filePath);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task Flush(CancellationToken cancellationToken, bool force)
    {
        if (!force && Interlocked.CompareExchange(ref _dirty, 0, 0) == 0)
        {
            return;
        }

        await _flushLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<PlayCountDto> countsToWrite;
            _lock.EnterReadLock();
            try
            {
                if (!force && Interlocked.Exchange(ref _dirty, 0) == 0)
                {
                    return;
                }

                if (force)
                {
                    Interlocked.Exchange(ref _dirty, 0);
                }

                countsToWrite = [.. _counts.Values];
            }
            finally
            {
                _lock.ExitReadLock();
            }

            string temporaryFilePath = _filePath + ".tmp";

            await using (FileStream stream = new(
                temporaryFilePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    countsToWrite,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);

                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryFilePath, _filePath, overwrite: true);
        }
        catch
        {
            Interlocked.Exchange(ref _dirty, 1);
            throw;
        }
        finally
        {
            _flushLock.Release();
        }
    }

    public readonly record struct PlayCountKey(Guid UserId, Guid ItemId);
}

public sealed record PlayCountDto(
    Guid UserId,
    Guid ItemId,
    int Count,
    DateTimeOffset? LastCountedUtc
);
