using System.Data;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using PythonScriptRunner.EntityFrameworkCore.Entities;

namespace PythonScriptRunner.EntityFrameworkCore.Runs;

/// <summary>Npgsql data source of the persistence package (shared by EF and the streaming file store).</summary>
internal sealed class PsrDataSource : IDisposable
{
    public PsrDataSource(PsrEntityFrameworkOptions options)
    {
        var builder = new NpgsqlDataSourceBuilder(options.ConnectionString);
        options.ConfigureDataSource?.Invoke(builder);
        DataSource = builder.Build();
    }

    public NpgsqlDataSource DataSource { get; }

    public void Dispose() => DataSource.Dispose();
}

/// <summary>
/// Files in <c>psr.artifacts</c> (metadata) and <c>psr.artifact_contents</c> (bytea, partitioned by day). Contents are
/// streamed with Npgsql in both directions, outside the change tracker, so a 50 MB file never sits in memory.
/// </summary>
internal sealed class ArtifactStore(
    IDbContextFactory<PsrDbContext> factory, PsrDataSource dataSource, PsrEntityFrameworkOptions options, TimeProvider time) : IArtifactStore
{
    public async Task<ArtifactInfo> SaveAsync(NewArtifact artifact, Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(content);
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var row = await SaveAsync(db, artifact, content, cancellationToken).ConfigureAwait(false);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return ToInfo(row, null);
            }
        }
    }

    /// <summary>
    /// Streams the content inside the open transaction of <paramref name="db"/> and adds the metadata row to it (saved by the caller).
    /// </summary>
    public async Task<ArtifactEntity> SaveAsync(PsrDbContext db, NewArtifact artifact, Stream content, CancellationToken cancellationToken)
    {
        var transaction = db.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction
            ?? throw new InvalidOperationException("Artifacts are stored inside a transaction.");
        var now = time.GetUtcNow();
        var id = artifact.Id ?? ArtifactId.New();
        Stream? spooled = null;
        try
        {
            if (!content.CanSeek)
            {
                // Npgsql streams only seekable values (it needs the length up front); spool the rest to disk.
                spooled = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920,
                    FileOptions.Asynchronous | FileOptions.DeleteOnClose);
                await content.CopyToAsync(spooled, cancellationToken).ConfigureAwait(false);
                spooled.Position = 0;
            }

            var hashing = new HashingStream(spooled ?? content);
            await using (hashing.ConfigureAwait(false))
            {
                var command = new NpgsqlCommand(
                    "INSERT INTO psr.artifact_contents (artifact_id, created_at, content) VALUES ($1, $2, $3)",
                    (NpgsqlConnection)db.Database.GetDbConnection(),
                    transaction);
                await using (command.ConfigureAwait(false))
                {
                    command.Parameters.Add(new NpgsqlParameter { Value = id.Value });
                    command.Parameters.Add(new NpgsqlParameter { Value = now });
                    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = hashing });
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                var retention = artifact.Retention is { } shorter && shorter < options.Retention.Artifacts ? shorter : options.Retention.Artifacts;
                var row = new ArtifactEntity
                {
                    Id = id.Value,
                    RunId = artifact.RunId.Value,
                    RunCreatedAt = artifact.RunId.CreatedAt,
                    StepRunId = artifact.StepRunId?.Value,
                    Direction = artifact.Direction,
                    FieldName = artifact.FieldName,
                    ListIndex = artifact.ListIndex,
                    FileName = artifact.FileName,
                    ContentType = artifact.ContentType,
                    SizeBytes = hashing.BytesRead,
                    Sha256 = hashing.Hash,
                    ContentCreatedAt = now,
                    ContentExpiresAt = now + retention,
                };
                db.Artifacts.Add(row);
                return row;
            }
        }
        finally
        {
            if (spooled is not null)
            {
                await spooled.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public async Task<ArtifactInfo?> GetAsync(ArtifactId artifactId, CancellationToken cancellationToken = default)
    {
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var row = await db.Artifacts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == artifactId.Value, cancellationToken).ConfigureAwait(false);
            if (row is null)
            {
                return null;
            }

            var stepKey = row.StepRunId is { } stepRunId
                ? await db.StepRuns.Where(s => s.Id == stepRunId && s.RunCreatedAt == row.RunCreatedAt).Select(s => s.StepKey)
                    .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
                : null;
            return ToInfo(row, stepKey);
        }
    }

    public async Task<Stream> OpenReadAsync(ArtifactId artifactId, CancellationToken cancellationToken = default)
    {
        var connection = await dataSource.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var command = new NpgsqlCommand(
                """
                SELECT a.content_expires_at, a.content_deleted_at, c.content
                FROM psr.artifacts a
                LEFT JOIN psr.artifact_contents c ON c.artifact_id = a.id AND c.created_at = a.content_created_at
                WHERE a.id = $1
                """,
                connection);
            command.Parameters.Add(new NpgsqlParameter { Value = artifactId.Value });
            var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess | CommandBehavior.SingleRow, cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new ArtifactNotFoundException(artifactId);
            }

            var expiresAt = reader.GetFieldValue<DateTimeOffset>(0);
            var deleted = !await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false);
            if (deleted || expiresAt <= time.GetUtcNow() || await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false))
            {
                throw new PsrDomainException(PsrErrorCodes.ArtifactExpired, $"The content of artifact {artifactId} was deleted by the retention.");
            }

            return new OwningStream(await reader.GetStreamAsync(2, cancellationToken).ConfigureAwait(false), reader, command, connection);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public static ArtifactInfo ToInfo(ArtifactEntity row, string? stepKey) => new()
    {
        Id = new ArtifactId(row.Id),
        RunId = new RunId(row.RunId),
        StepKey = stepKey,
        Direction = row.Direction,
        FieldName = row.FieldName,
        ListIndex = row.ListIndex,
        FileName = row.FileName,
        ContentType = row.ContentType,
        SizeBytes = row.SizeBytes,
        Sha256 = row.Sha256,
        ContentExpiresAt = row.ContentExpiresAt,
        ContentDeletedAt = row.ContentDeletedAt,
    };

    /// <summary>Reads the inner stream once while counting bytes and computing SHA-256; reports the inner length so Npgsql streams it.</summary>
    private sealed class HashingStream(Stream inner) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public long BytesRead { get; private set; }

        public string Hash => Convert.ToHexStringLower(_hash.GetCurrentHash());

        public override bool CanRead => true;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            Track(buffer[..read]);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            Track(buffer.Span[..read]);
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hash.Dispose();
            }

            base.Dispose(disposing);
        }

        private void Track(ReadOnlySpan<byte> data)
        {
            BytesRead += data.Length;
            _hash.AppendData(data);
        }
    }

    /// <summary>The content stream of a reader; disposing it releases the reader, the command and the connection.</summary>
    private sealed class OwningStream(Stream inner, NpgsqlDataReader reader, NpgsqlCommand command, NpgsqlConnection connection) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            await reader.DisposeAsync().ConfigureAwait(false);
            await command.DisposeAsync().ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                reader.Dispose();
                command.Dispose();
                connection.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
