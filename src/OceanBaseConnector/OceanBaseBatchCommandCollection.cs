#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Data.Common;

namespace OceanBaseConnector
{
    public sealed class OceanBaseBatchCommandCollection : DbBatchCommandCollection
    {
        private readonly OceanBaseBatch _owner;
        private readonly List<DbBatchCommand> _items = new List<DbBatchCommand>();
        internal OceanBaseBatchCommandCollection(OceanBaseBatch owner) => _owner = owner;
        public override int Count => _items.Count;
        public override bool IsReadOnly => false;
        public override IEnumerator<DbBatchCommand> GetEnumerator() => _items.GetEnumerator();
        public override bool Contains(DbBatchCommand item) => _items.Contains(item);
        public override int IndexOf(DbBatchCommand item) => _items.IndexOf(item);
        public override void CopyTo(DbBatchCommand[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);
        protected override DbBatchCommand GetBatchCommand(int index) => _items[index];
        public override void Add(DbBatchCommand item) => Insert(Count, item);
        public override void Insert(int index, DbBatchCommand item)
        {
            lock (_owner.Gate)
            {
                _owner.CheckMutable(); var command = Require(item);
                _items.Insert(index, command); command.Owner = _owner;
            }
        }
        protected override void SetBatchCommand(int index, DbBatchCommand item)
        {
            lock (_owner.Gate)
            {
                _owner.CheckMutable(); var previous = _items[index];
                if (ReferenceEquals(previous, item)) return;
                var command = Require(item); _items[index] = command; command.Owner = _owner;
                var removed = (OceanBaseBatchCommand)previous; removed.Owner = null; removed.Release();
            }
        }
        public override bool Remove(DbBatchCommand item)
        {
            lock (_owner.Gate)
            {
                _owner.CheckMutable(); var index = _items.IndexOf(item);
                if (index < 0) return false;
                RemoveAt(index); return true;
            }
        }
        public override void RemoveAt(int index)
        {
            lock (_owner.Gate)
            {
                _owner.CheckMutable(); var item = (OceanBaseBatchCommand)_items[index];
                _items.RemoveAt(index); item.Owner = null; item.Release();
            }
        }
        public override void Clear()
        {
            lock (_owner.Gate) { _owner.CheckMutable(); while (_items.Count != 0) RemoveAt(_items.Count - 1); }
        }
        private static OceanBaseBatchCommand Require(DbBatchCommand item)
        {
            var command = item as OceanBaseBatchCommand ?? throw new ArgumentException("An OceanBaseBatchCommand is required.", nameof(item));
            if (command.Owner != null) throw new ArgumentException("The batch command already belongs to a collection.", nameof(item));
            return command;
        }
    }
}
#endif
