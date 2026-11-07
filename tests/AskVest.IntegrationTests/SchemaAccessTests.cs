using AskVest.Platform;
using Microsoft.EntityFrameworkCore.Migrations;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests;

public sealed class SchemaAccessTests
{
    [Theory]
    [InlineData("Platform")]
    [InlineData("platform;drop")]
    [InlineData("platform-1")]
    [InlineData("1platform")]
    [InlineData("platform name")]
    [InlineData("")]
    public void InvalidSchemaName_IsRejected(string schema)
    {
        Should.Throw<ArgumentException>(() => SchemaAccess.ProvisionSchema(new MigrationBuilder("Npgsql"), schema, "history"));
    }

    [Theory]
    [InlineData("Bad-History")]
    [InlineData("history;drop")]
    public void InvalidHistoryTableName_IsRejected(string historyTable)
    {
        Should.Throw<ArgumentException>(() => SchemaAccess.ProvisionSchema(new MigrationBuilder("Npgsql"), "platform", historyTable));
    }

    [Fact]
    public void ValidNames_EmitGrants()
    {
        var builder = new MigrationBuilder("Npgsql");
        SchemaAccess.ProvisionSchema(builder, "module_schema", "__ef_migrations_history");
        builder.Operations.ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData("Bad-Schema")]
    [InlineData("schema;drop")]
    public void RestrictToAppendOnly_Rejects_InvalidNames(string name)
    {
        var builder = new MigrationBuilder("Npgsql");
        Should.Throw<ArgumentException>(() => SchemaAccess.RestrictToAppendOnly(builder, name, "table_name"));
        Should.Throw<ArgumentException>(() => SchemaAccess.RestrictToAppendOnly(builder, "schema_name", name));
    }

    [Fact]
    public void RestrictToAppendOnly_EmitsRevoke()
    {
        var builder = new MigrationBuilder("Npgsql");
        SchemaAccess.RestrictToAppendOnly(builder, "compliance", "compliance_events");
        builder.Operations.ShouldNotBeEmpty();
    }
}
