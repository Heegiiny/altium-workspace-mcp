using System.Reflection;
using System.Runtime.Serialization;
using System.ServiceModel;
using System.Xml;
using System.Xml.Serialization;
using AltiumWorkspaceMCP.Configuration;
using AltiumWorkspaceMCP.Vault;

namespace AltiumWorkspaceMCP.Diagnostics;

/// <summary>
/// <c>wire-dump</c> — a recording of the wire form of ALL operations of the vault service and all its types
/// without calling the server (to compare the proxies before and after regenerating them from the WSDL).
/// </summary>
/// <remarks>
/// Reads and writes on a live server (ALTIUM_SOAP_TRACE) cover only what the code calls.
/// Here, for each contract operation, a request is built in which every field is filled
/// with a predictable value (strings — by the field name, numbers — 1, collections — one element, etc.),
/// and it is sent to a closed local port: the WCF client records the message before sending (it is
/// picked up by <see cref="SoapTrace"/>), and the connection is refused — the production server is not touched.
/// Additionally each data type is recorded by what the code uses: <c>XmlSerializer</c>
/// (revision release scripts) and <c>DataContractSerializer</c>. It works with any set of proxies:
/// the types are found by reflection, so the same command takes the reference on the old proxies
/// and the result on the new ones, and tools/compare-soap-traces.py compares them.
/// The ALTIUM_SOAP_TRACE variable is required — the recording directory; ALTIUM_BASE_URL is not used.
/// </remarks>
public static class WireDumpCommand
{
    private const int MaxDepth = 5;

    public static async Task<int> RunAsync()
    {
        string? directory = Environment.GetEnvironmentVariable("ALTIUM_SOAP_TRACE");
        if (string.IsNullOrWhiteSpace(directory))
        {
            Console.Error.WriteLine("Set ALTIUM_SOAP_TRACE — the directory to record messages to.");
            return 2;
        }

        var options = new VaultOptions
        {
            // A closed local port: the production server is not touched by this command.
            BaseUrl = new Uri("http://127.0.0.1:1/"),
            StateDirectory = Path.Combine(Path.GetTempPath(), "altium-wire-dump"),
            ExchangeDirectory = Path.Combine(Path.GetTempPath(), "altium-wire-dump", "exchange"),
            Timeout = TimeSpan.FromSeconds(10),
        };

        var endpoints = new VaultEndpoints(options);
        Type clientType = typeof(VaultEndpoints).GetMethod(nameof(VaultEndpoints.CreateVaultClient))!.ReturnType;
        Type contract = clientType.GetInterfaces().Single(candidate => candidate.Name == "IVaultActionService");
        Assembly assembly = clientType.Assembly;
        string? ns = clientType.Namespace;

        // 1. Operations: each has a filled request.
        var operations = contract.GetMethods()
            .Where(method => method.Name.EndsWith("Async", StringComparison.Ordinal)
                && method.GetParameters() is [{ } parameter]
                && parameter.ParameterType.Name.EndsWith("Request", StringComparison.Ordinal))
            .OrderBy(method => method.Name, StringComparer.Ordinal)
            .ToList();

        int sent = 0;
        foreach (MethodInfo method in operations)
        {
            object? request = Populate(method.GetParameters()[0].ParameterType, "request", 0, []);
            var client = endpoints.CreateVaultClient();
            try
            {
                await (Task)method.Invoke(client, [request])!;
            }
            catch (Exception exception) when (exception is CommunicationException or TimeoutException)
            {
                // Expected: the port is closed. The message is already recorded.
            }
            finally
            {
                client.Abort();
            }

            sent++;
        }

        // 2. Data types: what the code builds scripts (XmlSerializer) and messages (DataContractSerializer) with.
        var types = assembly.GetTypes()
            .Where(type => type.Namespace == ns
                && type.IsClass && !type.IsAbstract
                && type.GetCustomAttribute<DataContractAttribute>() is not null
                && type.GetConstructor(Type.EmptyTypes) is not null)
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToList();

        int index = 0;
        foreach (Type type in types)
        {
            object? value = Populate(type, "value", 0, []);
            if (value is null)
            {
                continue;
            }

            index++;
            Write(directory, index, "xml", type.Name, () => new XmlSerializer(type, new XmlRootAttribute("item")), value);
            Write(directory, index, "dcs", type.Name, () => null, value);
        }

        // 3. Contract description: the serialization attributes of each type and operation — what is not visible
        // from the filled values (required flags, defaults, serialization callbacks).
        int meta = 0;
        foreach (Type type in assembly.GetTypes().Where(candidate => candidate.Namespace == ns).OrderBy(candidate => candidate.Name, StringComparer.Ordinal))
        {
            if (type.Name.StartsWith('<') || type.Name.Contains('+'))
            {
                continue;
            }

            meta++;
            WriteMeta(Path.Combine(directory, $"{8000 + meta:D6}-meta-{type.Name}.req.xml"), type);
        }

        Console.Error.WriteLine($"Operations: {sent}, data types: {types.Count}, type descriptions: {meta}. Directory: {directory}");
        return 0;
    }

    /// <summary>Serialization and contract attributes of a type: members in declaration order (XmlSerializer uses it).</summary>
    private static void WriteMeta(string file, Type type)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        using XmlWriter writer = XmlWriter.Create(file, new XmlWriterSettings { Indent = false });
        writer.WriteStartElement("type");
        writer.WriteAttributeString("name", type.Name);
        writer.WriteAttributeString("kind", type.IsEnum ? "enum" : type.IsInterface ? "interface" : type.IsAbstract ? "abstract" : "class");
        writer.WriteAttributeString("base", type.BaseType?.Name ?? "");
        writer.WriteAttributeString("interfaces", string.Join(",", type.GetInterfaces().Select(i => i.Name).OrderBy(n => n, StringComparer.Ordinal)));

        foreach (CustomAttributeData attribute in type.CustomAttributes
            .Where(a => a.AttributeType.Name != "GeneratedCodeAttribute" && a.AttributeType.Name != "DebuggerStepThroughAttribute")
            .OrderBy(a => a.AttributeType.Name, StringComparer.Ordinal).ThenBy(Describe, StringComparer.Ordinal))
        {
            writer.WriteElementString("attribute", Describe(attribute));
        }

        foreach (FieldInfo field in type.GetFields(all).Where(f => !f.Name.EndsWith("Field", StringComparison.Ordinal) || f.IsPublic))
        {
            writer.WriteStartElement("field");
            writer.WriteAttributeString("name", field.Name);
            writer.WriteAttributeString("type", TypeName(field.FieldType));
            foreach (CustomAttributeData attribute in field.CustomAttributes.OrderBy(Describe, StringComparer.Ordinal))
            {
                writer.WriteElementString("attribute", Describe(attribute));
            }

            writer.WriteEndElement();
        }

        foreach (PropertyInfo property in type.GetProperties(all))
        {
            writer.WriteStartElement("property");
            writer.WriteAttributeString("name", property.Name);
            writer.WriteAttributeString("type", TypeName(property.PropertyType));
            writer.WriteAttributeString("access", (property.CanRead ? "r" : "") + (property.CanWrite ? "w" : ""));
            foreach (CustomAttributeData attribute in property.CustomAttributes.OrderBy(Describe, StringComparer.Ordinal))
            {
                writer.WriteElementString("attribute", Describe(attribute));
            }

            writer.WriteEndElement();
        }

        foreach (MethodInfo method in type.GetMethods(all).Where(m => !m.IsSpecialName))
        {
            writer.WriteStartElement("method");
            writer.WriteAttributeString("name", method.Name);
            writer.WriteAttributeString("returns", TypeName(method.ReturnType));
            writer.WriteAttributeString("parameters", string.Join(",", method.GetParameters().Select(p => TypeName(p.ParameterType) + " " + p.Name)));
            foreach (CustomAttributeData attribute in method.CustomAttributes
                .Where(a => a.AttributeType.Name != "DebuggerStepThroughAttribute").OrderBy(Describe, StringComparer.Ordinal))
            {
                writer.WriteElementString("attribute", Describe(attribute));
            }

            writer.WriteEndElement();
        }

        if (type.IsEnum)
        {
            foreach (FieldInfo value in type.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                writer.WriteElementString("value", value.Name + "=" + Convert.ToInt64(value.GetRawConstantValue()));
            }
        }

        writer.WriteEndElement();
    }

    private static string TypeName(Type type) => type.IsGenericType
        ? type.Name + "<" + string.Join(",", type.GetGenericArguments().Select(TypeName)) + ">"
        : type.Name;

    private static string Describe(CustomAttributeData attribute) =>
        attribute.AttributeType.Name + "("
        + string.Join(",", attribute.ConstructorArguments.Select(a => a.Value is Type t ? TypeName(t) : a.Value?.ToString()))
        + string.Join(",", attribute.NamedArguments.OrderBy(a => a.MemberName, StringComparer.Ordinal)
            .Select(a => (attribute.ConstructorArguments.Count > 0 ? "," : "") + a.MemberName + "=" + (a.TypedValue.Value is Type t ? TypeName(t) : a.TypedValue.Value)))
        + ")";

    private static void Write(string directory, int index, string kind, string name, Func<XmlSerializer?> serializer, object value)
    {
        string file = Path.Combine(directory, $"{9000 + index:D6}-{kind}-{name}.req.xml");
        try
        {
            using XmlWriter writer = XmlWriter.Create(file, new XmlWriterSettings { Indent = false });
            if (serializer() is { } xml)
            {
                xml.Serialize(writer, value);
            }
            else
            {
                new DataContractSerializer(value.GetType()).WriteObject(writer, value);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or SerializationException)
        {
            File.WriteAllText(file, $"<NotSerializable>{System.Security.SecurityElement.Escape(exception.GetBaseException().Message)}</NotSerializable>");
        }
    }

    /// <summary>Fills an object with predictable values: each field has its own, so that a field swap is visible.</summary>
    private static object? Populate(Type type, string name, int depth, HashSet<Type> chain)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        if (type == typeof(string)) return "s_" + name;
        if (type == typeof(bool)) return true;
        if (type == typeof(int)) return 1;
        if (type == typeof(long)) return 2L;
        if (type == typeof(short)) return (short)3;
        if (type == typeof(byte)) return (byte)4;
        if (type == typeof(double)) return 1.5;
        if (type == typeof(float)) return 2.5f;
        if (type == typeof(decimal)) return 3.5m;
        if (type == typeof(DateTime)) return new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        if (type == typeof(Guid)) return new Guid("00000000-0000-0000-0000-00000000000a");
        if (type == typeof(TimeSpan)) return TimeSpan.FromSeconds(7);
        if (type == typeof(byte[])) return new byte[] { 1, 2, 3 };

        if (type.IsEnum)
        {
            Array values = Enum.GetValues(type);
            return values.Length == 0 ? null : values.GetValue(values.Length - 1);
        }

        if (depth >= MaxDepth || !chain.Add(type))
        {
            return null;
        }

        try
        {
            if (type.IsArray)
            {
                Type element = type.GetElementType()!;
                Array array = Array.CreateInstance(element, 1);
                array.SetValue(Populate(element, name, depth + 1, chain), 0);
                return array;
            }

            if (type.GetConstructor(Type.EmptyTypes) is null || type.IsAbstract)
            {
                return null;
            }

            object instance = Activator.CreateInstance(type)!;

            if (ListElement(type) is { } elementType && instance is System.Collections.IList list)
            {
                object? item = Populate(elementType, name, depth + 1, chain);
                if (item is not null)
                {
                    list.Add(item);
                }

                return instance;
            }

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                field.SetValue(instance, Populate(field.FieldType, field.Name, depth + 1, chain));
            }

            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.CanWrite && property.GetIndexParameters().Length == 0
                    && property.PropertyType != typeof(ExtensionDataObject))
                {
                    property.SetValue(instance, Populate(property.PropertyType, property.Name, depth + 1, chain));
                }
            }

            return instance;
        }
        finally
        {
            chain.Remove(type);
        }
    }

    private static Type? ListElement(Type type)
    {
        for (Type? current = type; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(List<>))
            {
                return current.GetGenericArguments()[0];
            }
        }

        return null;
    }
}
