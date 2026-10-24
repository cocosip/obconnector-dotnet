using System;
using System.Collections;
using System.Collections.Generic;
using System.Data.Common;

namespace OceanBaseConnector
{
    public sealed class OceanBaseParameterCollection : DbParameterCollection
    {
        private readonly object _gate = new object();
        private List<OceanBaseParameter> _items = new List<OceanBaseParameter>();
        private int _version;
        internal OceanBaseParameterCollection() { }
        internal int Version { get { lock (_gate) return _version; } }
        public override int Count { get { lock (_gate) return _items.Count; } }
        public override object SyncRoot => _gate;
        public override int Add(object value)
        {
            lock (_gate)
            {
                var parameter = Require(value); CheckOwner(parameter);
                _items.Add(parameter); parameter.Owner = this; _version = unchecked(_version + 1);
                return _items.Count - 1;
            }
        }
        public override void AddRange(Array values)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            lock (_gate)
            {
                var pending = new HashSet<OceanBaseParameter>();
                foreach (var value in values)
                {
                    var parameter = Require(value!); CheckOwner(parameter);
                    if (!pending.Add(parameter)) throw new ArgumentException("A parameter cannot occur twice.", nameof(values));
                }
                foreach (var value in values) Add(value!);
            }
        }
        public override void Clear()
        {
            lock (_gate)
            {
                foreach (var item in _items) item.Owner = null;
                _items.Clear(); _version = unchecked(_version + 1);
            }
        }
        public override bool Contains(object value) { lock (_gate) return value is OceanBaseParameter parameter && _items.Contains(parameter); }
        public override bool Contains(string value) => IndexOf(value) >= 0;
        public override void CopyTo(Array array, int index) { lock (_gate) ((ICollection)_items).CopyTo(array, index); }
        public override IEnumerator GetEnumerator() { lock (_gate) return ((IEnumerable)_items.ToArray()).GetEnumerator(); }
        public override int IndexOf(object value) { lock (_gate) return value is OceanBaseParameter parameter ? _items.IndexOf(parameter) : -1; }
        public override int IndexOf(string parameterName)
        {
            lock (_gate)
            {
                // Mode-specific bind normalization is performed by the execution layer, not this collection.
                for (var i = 0; i < _items.Count; i++) if (string.Equals(_items[i].ParameterName, parameterName, StringComparison.Ordinal)) return i;
                for (var i = 0; i < _items.Count; i++) if (string.Equals(_items[i].ParameterName, parameterName, StringComparison.OrdinalIgnoreCase)) return i;
                return -1;
            }
        }
        public override void Insert(int index, object value)
        {
            lock (_gate)
            {
                var parameter = Require(value); CheckOwner(parameter);
                _items.Insert(index, parameter); parameter.Owner = this; _version = unchecked(_version + 1);
            }
        }
        public override void Remove(object value)
        {
            lock (_gate)
            {
                var index = IndexOf(value);
                if (index < 0) throw new ArgumentException("Parameter is not in this collection.", nameof(value));
                RemoveAt(index);
            }
        }
        public override void RemoveAt(int index)
        {
            lock (_gate)
            {
                var item = _items[index]; _items.RemoveAt(index); item.Owner = null; _version = unchecked(_version + 1);
            }
        }
        public override void RemoveAt(string parameterName) { lock (_gate) RemoveAt(RequireIndex(parameterName)); }
        protected override DbParameter GetParameter(int index) { lock (_gate) return _items[index]; }
        protected override DbParameter GetParameter(string parameterName) { lock (_gate) return _items[RequireIndex(parameterName)]; }
        protected override void SetParameter(string parameterName, DbParameter value) { lock (_gate) SetParameter(RequireIndex(parameterName), value); }
        protected override void SetParameter(int index, DbParameter value)
        {
            lock (_gate)
            {
                var previous = _items[index]; var parameter = Require(value);
                if (ReferenceEquals(previous, parameter)) return;
                CheckOwner(parameter); previous.Owner = null;
                _items[index] = parameter; parameter.Owner = this; _version = unchecked(_version + 1);
            }
        }
        internal void ReplaceDerived(OceanBaseParameter[] parameters, int expectedVersion)
        {
            var pending = new List<OceanBaseParameter>(parameters.Length);
            var unique = new HashSet<OceanBaseParameter>();
            foreach (var parameter in parameters)
            {
                CheckOwner(parameter);
                if (!unique.Add(parameter)) throw new ArgumentException("A parameter cannot occur twice.", nameof(parameters));
                pending.Add(parameter);
            }
            lock (_gate)
            {
                if (_version != expectedVersion) throw new InvalidOperationException("The parameter collection changed during derivation.");
                foreach (var item in _items) item.Owner = null;
                foreach (var item in pending) item.Owner = this;
                _items = pending; _version = unchecked(_version + 1);
            }
        }
        private int RequireIndex(string name) { var index = IndexOf(name); return index >= 0 ? index : throw new IndexOutOfRangeException("Parameter name was not found."); }
        private static OceanBaseParameter Require(object value) => value as OceanBaseParameter ?? throw new ArgumentException("An OceanBaseParameter is required.", nameof(value));
        private static void CheckOwner(OceanBaseParameter parameter) { if (parameter.Owner != null) throw new ArgumentException("Parameter already belongs to a collection."); }
    }
}
