namespace PythonScriptRunner.Core.Pipelines;

/// <summary>Which source types can feed which target types in a binding (spec 003, safe widenings only).</summary>
internal static class TypeCompatibility
{
    public static bool IsAssignable(FieldDefinition source, FieldDefinition target) =>
        IsAssignable(source.Type, target.Type, source, target);

    private static bool IsAssignable(FieldType source, FieldType target, FieldDefinition sourceField, FieldDefinition targetField)
    {
        if (target.Kind == FieldKind.Json)
        {
            return !source.IsFile;
        }

        return (source.Kind, target.Kind) switch
        {
            (FieldKind.Integer, FieldKind.Number or FieldKind.Decimal) => true,
            (FieldKind.Enum, FieldKind.String) => true,
            (FieldKind.Enum, FieldKind.Enum) => source.EnumValues!.All(v => target.EnumValues!.Contains(v, StringComparer.Ordinal)),
            (FieldKind.Table, FieldKind.Table) => ColumnsCompatible(sourceField.Columns, targetField.Columns),
            (FieldKind.File, FieldKind.File) => ContentTypesCompatible(sourceField.ContentTypes, targetField.ContentTypes),
            (FieldKind.List, FieldKind.List) => IsAssignable(source.ItemType!, target.ItemType!, sourceField, targetField),
            var (s, t) when s == t => true,
            _ => false,
        };
    }

    private static bool ColumnsCompatible(IReadOnlyList<TableColumn>? source, IReadOnlyList<TableColumn>? target)
    {
        if (target is not { Count: > 0 })
        {
            return true;
        }

        if (source is not { Count: > 0 })
        {
            return false;
        }

        return target.All(t => source.Any(s => s.Name == t.Name
            && IsAssignable(s.Type, t.Type, new FieldDefinition(s.Name, s.Type), new FieldDefinition(t.Name, t.Type))));
    }

    private static bool ContentTypesCompatible(IReadOnlyList<string>? source, IReadOnlyList<string>? target) =>
        source is not { Count: > 0 } || target is not { Count: > 0 }
        || source.Intersect(target, StringComparer.OrdinalIgnoreCase).Any();
}
