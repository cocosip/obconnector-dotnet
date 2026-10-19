using System;
using System.Collections.Generic;
using OceanBaseConnector.Internal.Session;
using OceanBaseConnector.Internal.Values;

namespace OceanBaseConnector
{
    /// <summary>An immutable Oracle OBJECT/COLLECTION descriptor; dictionary and factory types retain their session.</summary>
    public sealed class OceanBaseComplexType
    {
        internal OceanBaseComplexType(ComplexTypeContext? context, string schema, string name, bool collection, OceanBaseComplexMember[] members)
        {
            Context = context; SchemaName = schema; TypeName = name; IsCollection = collection;
            Members = Array.AsReadOnly(members); var layouts = new ComplexType[members.Length]; NestingDepth = 1;
            for (var i = 0; i < layouts.Length; i++)
            {
                layouts[i] = members[i].Layout;
                NestingDepth = Math.Max(NestingDepth, 1 + (members[i].ComplexType?.NestingDepth ?? 0));
            }
            Layout = new ComplexType(collection, layouts);
        }

        public string SchemaName { get; }
        public string TypeName { get; }
        public bool IsCollection { get; }
        /// <summary>True for a collection with an element layout and no named database type identity.</summary>
        public bool IsAnonymous => TypeName.Length == 0;
        /// <summary>Ordered object attributes, or the collection's single element descriptor.</summary>
        public IReadOnlyList<OceanBaseComplexMember> Members { get; }
        /// <summary>Creates a checked value snapshot; nested values must use the corresponding member type.</summary>
        public OceanBaseComplexValue CreateValue(params object?[] values)
        {
            if (Context == null) throw new NotSupportedException("Anonymous result descriptors cannot construct parameter values.");
            Context.Connection.ValidateComplexType(this);
            var result = new OceanBaseComplexValue(this, values);
            ComplexValueCodec.EncodeParameter(Layout, result.WireValues(), OceanBaseCompatibilityMode.Oracle);
            Context.Connection.ValidateComplexType(this); return result;
        }
        internal ComplexType Layout { get; }
        internal int NestingDepth { get; }
        internal ComplexTypeContext? Context { get; }
    }

    /// <summary>An object attribute or a collection element, preserving its exact dictionary identity.</summary>
    public sealed class OceanBaseComplexMember
    {
        internal OceanBaseComplexMember(string? name, string? schema, string typeName, OceanBaseComplexType? complex, ComplexType layout)
        { Name = name; SchemaName = schema; TypeName = typeName; ComplexType = complex; Layout = layout; }
        /// <summary>Attribute name; null for a collection element.</summary>
        public string? Name { get; }
        public string? SchemaName { get; }
        public string TypeName { get; }
        /// <summary>Nested named type; null for a supported scalar member.</summary>
        public OceanBaseComplexType? ComplexType { get; }
        internal ComplexType Layout { get; }
    }
}
