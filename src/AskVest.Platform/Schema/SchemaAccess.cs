using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AskVest.Platform;

public static partial class SchemaAccess
{
    private const string AppRole = "askvest_app";
    private const string OwnerRole = "askvest_owner";

    public static void ProvisionSchema(MigrationBuilder migrationBuilder, string schema, string historyTable)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ValidateName(schema, nameof(schema));
        ValidateName(historyTable, nameof(historyTable));

        migrationBuilder.Sql($$"""
            GRANT USAGE ON SCHEMA {{schema}} TO {{AppRole}};
            ALTER DEFAULT PRIVILEGES FOR ROLE {{OwnerRole}} IN SCHEMA {{schema}}
              GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO {{AppRole}};
            ALTER DEFAULT PRIVILEGES FOR ROLE {{OwnerRole}} IN SCHEMA {{schema}}
              GRANT USAGE, SELECT ON SEQUENCES TO {{AppRole}};
            GRANT SELECT ON TABLE {{schema}}.{{historyTable}} TO {{AppRole}};
            """);
    }

    private static void ValidateName(string name, string parameterName)
    {
        if (!NamePattern().IsMatch(name))
        {
            throw new ArgumentException($"Schema object name '{name}' must match ^[a-z_]+$.", parameterName);
        }
    }

    [GeneratedRegex("^[a-z_]+$")]
    private static partial Regex NamePattern();
}
