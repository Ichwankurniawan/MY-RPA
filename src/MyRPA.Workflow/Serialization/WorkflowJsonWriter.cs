using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using MyRPA.Workflow.Values;

namespace MyRPA.Workflow.Serialization;

/// <summary>Writes a <see cref="WorkflowDefinition"/> in the versioned JSON format (ADR-0011). Load → write round-trips.</summary>
public static class WorkflowJsonWriter
{
    /// <summary>
    /// Writer options for workflow files and CLI output: indented, and without HTML-oriented escaping so expressions
    /// such as <c>'a' + b</c> stay readable. Workflow JSON is never embedded in HTML by MyRPA.
    /// </summary>
    public static JsonWriterOptions Options { get; } = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Serializes the workflow to indented JSON text.</summary>
    /// <param name="workflow">The workflow.</param>
    public static string Write(WorkflowDefinition workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, Options))
        {
            Write(writer, workflow);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Serializes the workflow to a JSON writer.</summary>
    /// <param name="writer">Destination.</param>
    /// <param name="workflow">The workflow.</param>
    public static void Write(Utf8JsonWriter writer, WorkflowDefinition workflow)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(workflow);

        writer.WriteStartObject();
        writer.WriteString("schemaVersion", workflow.SchemaVersion.ToString());
        writer.WriteString("id", workflow.Id.Value);
        writer.WriteString("name", workflow.Name);
        writer.WriteString("version", workflow.Version);
        if (workflow.Description is not null)
        {
            writer.WriteString("description", workflow.Description);
        }

        if (workflow.Arguments.Count > 0)
        {
            writer.WriteStartArray("arguments");
            foreach (var argument in workflow.Arguments)
            {
                writer.WriteStartObject();
                writer.WriteString("name", argument.Name);
                writer.WriteString("direction", argument.Direction.ToString());
                writer.WriteString("type", argument.Type.ToString());
                if (argument.IsRequired)
                {
                    writer.WriteBoolean("required", true);
                }

                if (argument.DefaultValue is not null)
                {
                    writer.WritePropertyName("default");
                    WorkflowValues.WriteJson(writer, argument.DefaultValue);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        if (workflow.Variables.Count > 0)
        {
            writer.WriteStartArray("variables");
            foreach (var variable in workflow.Variables)
            {
                writer.WriteStartObject();
                writer.WriteString("name", variable.Name);
                writer.WriteString("type", variable.Type.ToString());
                if (variable.DefaultValue is not null)
                {
                    writer.WritePropertyName("default");
                    WorkflowValues.WriteJson(writer, variable.DefaultValue);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        writer.WritePropertyName("root");
        WriteNode(writer, workflow.Root);
        writer.WriteEndObject();
    }

    private static void WriteNode(Utf8JsonWriter writer, NodeDefinition node)
    {
        writer.WriteStartObject();
        writer.WriteString("id", node.Id.Value);
        writer.WriteString("type", node.Type.Value);
        if (node.DisplayName is not null)
        {
            writer.WriteString("displayName", node.DisplayName);
        }

        if (node.Properties.Count > 0)
        {
            writer.WriteStartObject("properties");
            foreach (var (name, value) in node.Properties)
            {
                writer.WritePropertyName(name);
                WriteProperty(writer, value);
            }

            writer.WriteEndObject();
        }

        if (node.Children.Count > 0)
        {
            writer.WriteStartArray("children");
            foreach (var child in node.Children)
            {
                WriteNode(writer, child);
            }

            writer.WriteEndArray();
        }

        if (node.Slots.Count > 0)
        {
            writer.WriteStartObject("slots");
            foreach (var (name, slot) in node.Slots)
            {
                writer.WritePropertyName(name);
                WriteNode(writer, slot);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static void WriteProperty(Utf8JsonWriter writer, PropertyValue value)
    {
        switch (value)
        {
            case ExpressionPropertyValue e when e.Expression.IsJsonLiteral:
                writer.WriteRawValue(e.Expression.Source);
                break;
            case ExpressionPropertyValue e:
                writer.WriteStringValue(e.Expression.Source);
                break;
            case TextPropertyValue t:
                writer.WriteStringValue(t.Text);
                break;
            case NamePropertyValue n:
                writer.WriteStringValue(n.Name);
                break;
            case ExpressionMapPropertyValue map:
                writer.WriteStartObject();
                foreach (var (key, expression) in map.Entries)
                {
                    writer.WritePropertyName(key);
                    if (expression.IsJsonLiteral)
                    {
                        writer.WriteRawValue(expression.Source);
                    }
                    else
                    {
                        writer.WriteStringValue(expression.Source);
                    }
                }

                writer.WriteEndObject();
                break;
            case NameMapPropertyValue map:
                writer.WriteStartObject();
                foreach (var (key, name) in map.Entries)
                {
                    writer.WriteString(key, name);
                }

                writer.WriteEndObject();
                break;
            default:
                throw new ArgumentException($"Unsupported property value '{value.GetType().Name}'.", nameof(value));
        }
    }
}
