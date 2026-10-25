using System.Text;

namespace OceanBaseConnector.Internal.Session
{
    internal sealed class ComplexTypeContext
    {
        internal ComplexTypeContext(OceanBaseConnection connection, PhysicalSession session, OceanBaseTransaction? transaction, long epoch, int timeout)
        { Connection = connection; Session = session; Transaction = transaction; Epoch = epoch; Timeout = timeout; MetadataEpoch = session.ComplexTypeEpoch; }
        internal OceanBaseConnection Connection { get; }
        internal PhysicalSession Session { get; }
        internal OceanBaseTransaction? Transaction { get; }
        internal long Epoch { get; }
        internal long MetadataEpoch { get; }
        internal int Timeout { get; }
        internal Encoding? StoredNationalEncoding { get; set; }
    }
}
