using System.Text;
using PythonScriptRunner.Core.Execution;

namespace PythonScriptRunner.Core.Tests.Unit;

public sealed class RetryPolicyTests
{
    [Fact]
    public void GetDelay_DefaultsToTenSecondsDoubling_CappedAtTenMinutes()
    {
        var policy = RetryPolicy.None with { MaxAttempts = 5 };

        policy.GetDelay(1).ShouldBe(TimeSpan.FromSeconds(10));
        policy.GetDelay(2).ShouldBe(TimeSpan.FromSeconds(20));
        policy.GetDelay(10).ShouldBe(RetryPolicy.MaxDelay);
    }
}

public sealed class LogCaptureTests
{
    [Fact]
    public void TailBuffer_KeepsOnlyTheTail()
    {
        var buffer = new TailBuffer(4);

        buffer.Append("abc"u8);
        buffer.Append("defg"u8);
        buffer.Append("h"u8);
        var tail = buffer.ToLogTail(SecretMasker.None);

        tail.Content.ShouldBe("efgh");
        tail.Truncated.ShouldBeTrue();
        tail.OriginalBytes.ShouldBe(8);
    }

    [Fact]
    public void TailBuffer_WithoutOverflow_IsNotTruncated()
    {
        var buffer = new TailBuffer(16);

        buffer.Append(Encoding.UTF8.GetBytes("olá"));

        buffer.ToLogTail(SecretMasker.None).ShouldBe(new LogTail("olá", false, 4));
    }

    [Fact]
    public void SecretMasker_ReplacesValues_IgnoringTinyOnes()
    {
        var masker = new SecretMasker(["s3cr3t", "ab"]);

        masker.Mask("password=s3cr3t ab").ShouldBe("password=*** ab");
    }
}

public sealed class FieldTypeTests
{
    [Fact]
    public void Parse_RoundTripsEveryType()
    {
        FieldType[] types =
        [
            FieldType.Number, FieldType.Integer, FieldType.Decimal, FieldType.String, FieldType.Bool, FieldType.Date,
            FieldType.DateTime, FieldType.Json, FieldType.Table, FieldType.File, FieldType.Enum("a", "b"),
            FieldType.ListOf(FieldType.Enum("x")), FieldType.ListOf(FieldType.File),
        ];

        foreach (var type in types)
        {
            FieldType.Parse(type.Kind == FieldKind.List ? "list" : type.WireName, type.ItemType?.WireName, type.EnumValues).ShouldBe(type);
        }
    }

    [Fact]
    public void ListOf_RejectsNestedListsAndTables()
    {
        Should.Throw<ArgumentException>(() => FieldType.ListOf(FieldType.Table));
        Should.Throw<ArgumentException>(() => FieldType.ListOf(FieldType.ListOf(FieldType.Number)));
    }
}
