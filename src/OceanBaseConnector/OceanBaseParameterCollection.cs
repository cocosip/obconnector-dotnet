using System;
using System.Collections;
using System.Collections.Generic;
using System.Data.Common;

namespace OceanBaseConnector
{
    public sealed class OceanBaseParameterCollection : DbParameterCollection
    {
        private readonly List<OceanBaseParameter> _items = new List<OceanBaseParameter>();
        internal OceanBaseParameterCollection() { }
        public override int Count => _items.Count;
        public override object SyncRoot => ((ICollection)_items).SyncRoot;
        public override int Add(object value)
        {
            var parameter = Require(value);
            CheckOwner(parameter);
            parameter.Owner = this;
            _items.Add(parameter);
            return _items.Count - 1;
        }
        public override void AddRange(Array values)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            var pending = new HashSet<OceanBaseParameter>();
            foreach (var value in values)
            {
                var parameter = Require(value!);
                CheckOwner(parameter);
                if (!pending.Add(parameter)) throw new ArgumentException("A parameter cannot occur twice.", nameof(values));
            }
            foreach (var value in values) Add(value!);
        }
        public override void Clear() { foreach (var item in _items) item.Owner = null; _items.Clear(); }
        public override bool Contains(object value) => value is OceanBaseParameter parameter && _items.Contains(parameter);
        public override bool Contains(string value) => IndexOf(value) >= 0;
        public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);
        public override IEnumerator GetEnumerator() => _items.GetEnumerator();
        public override int IndexOf(object value) => value is OceanBaseParameter parameter ? _items.IndexOf(parameter) : -1;
        public override int IndexOf(string parameterName)
        {
            // Mode-specific bind normalization is performed by the execution layer, not this collection.
            for (var i = 0; i < _items.Count; i++) if (string.Equals(_items[i].ParameterName, parameterName, StringComparison.Ordinal)) return i;
            for (var i = 0; i < _items.Count; i++) if (string.Equals(_items[i].ParameterName, parameterName, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }
        public override void Insert(int index, object value)
        {
            var parameter = Require(value);
            CheckOwner(parameter);
            _items.Insert(index, parameter);
            parameter.Owner = this;
        }
        public override void Remove(object value)
        {
            var index = IndexOf(value);
            if (index < 0) throw new ArgumentException("Parameter is not in this collection.", nameof(value));
            RemoveAt(index);
        }
        public override void RemoveAt(int index) { var item = _items[index]; _items.RemoveAt(index); item.Owner = null; }
        public override void RemoveAt(string parameterName) => RemoveAt(RequireIndex(parameterName));
        protected override DbParameter GetParameter(int index) => _items[index];
        protected override DbParameter GetParameter(string parameterName) => _items[RequireIndex(parameterName)];
        protected override void SetParameter(string parameterName, DbParameter value) => SetParameter(RequireIndex(parameterName), value);
        protected override void SetParameter(int index, DbParameter value)
        {
            var previous = _items[index];
            var parameter = Require(value);
            if (ReferenceEquals(previous, parameter)) return;
            CheckOwner(parameter);
            previous.Owner = null;
            _items[index] = parameter;
            parameter.Owner = this;
        }
        private int RequireIndex(string name) { var index = IndexOf(name); return index >= 0 ? index : throw new IndexOutOfRangeException("Parameter name was not found."); }
        private static OceanBaseParameter Require(object value) => value as OceanBaseParameter ?? throw new ArgumentException("An OceanBaseParameter is required.", nameof(value));
        private static void CheckOwner(OceanBaseParameter parameter) { if (parameter.Owner != null) throw new ArgumentException("Parameter already belongs to a collection."); }
    }
}
