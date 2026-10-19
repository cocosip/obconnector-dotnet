using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class XidTests
    {
        [Theory]
        [InlineData(int.MinValue)]
        [InlineData(-2)]
        [InlineData(0)]
        [InlineData(int.MaxValue)]
        public void XidPreservesTheFormatIdentifierAndArbitraryBytes(int formatId)
        {
            var global = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
            var branch = Enumerable.Range(192, 64).Select(i => (byte)i).ToArray();
            var xid = new OceanBaseXid(formatId, global, branch);
            Assert.Equal(formatId, xid.FormatId);
            Assert.Equal(global, xid.GetGlobalTransactionId());
            Assert.Equal(branch, xid.GetBranchQualifier());
        }

        [Fact]
        public void XidRejectsNullIdentifiersAndTheReservedNullFormat()
        {
            Assert.Equal("globalTransactionId", Assert.Throws<ArgumentNullException>(() => new OceanBaseXid(1, null!, Array.Empty<byte>())).ParamName);
            Assert.Equal("branchQualifier", Assert.Throws<ArgumentNullException>(() => new OceanBaseXid(1, new byte[] { 1 }, null!)).ParamName);
            Assert.Equal("formatId", Assert.Throws<ArgumentOutOfRangeException>(() => new OceanBaseXid(-1, new byte[] { 1 }, Array.Empty<byte>())).ParamName);
        }

        [Theory]
        [InlineData(0, 0, "globalTransactionId")]
        [InlineData(65, 0, "globalTransactionId")]
        [InlineData(1, 65, "branchQualifier")]
        public void XidRejectsLengthsOutsideTheProtocolLimits(int globalLength, int branchLength, string parameter)
        {
            var error = Assert.Throws<ArgumentException>(() => new OceanBaseXid(1, new byte[globalLength], new byte[branchLength]));
            Assert.Equal(parameter, error.ParamName);
        }

        [Fact]
        public void XidCopiesInputsAndEveryReturnedArraySoDictionaryIdentityCannotChange()
        {
            var global = new byte[] { 0, 39, 92, 255 };
            var branch = new byte[] { 254, 0 };
            var xid = new OceanBaseXid(7, global, branch);
            var expected = new OceanBaseXid(7, (byte[])global.Clone(), (byte[])branch.Clone());
            var dictionary = new Dictionary<OceanBaseXid, string> { [xid] = "registered" };
            var hash = xid.GetHashCode();
            global[0] = 99; branch[0] = 99;
            var returnedGlobal = xid.GetGlobalTransactionId();
            var returnedBranch = xid.GetBranchQualifier();
            returnedGlobal[0] = 88; returnedBranch[0] = 88;
            Assert.NotSame(returnedGlobal, xid.GetGlobalTransactionId());
            Assert.NotSame(returnedBranch, xid.GetBranchQualifier());
            Assert.Equal(expected, xid);
            Assert.Equal(hash, xid.GetHashCode());
            Assert.Equal("registered", dictionary[expected]);
        }

        [Fact]
        public void XidEqualityKeepsTheTwoIdentifierBoundariesAndAllFields()
        {
            var xid = new OceanBaseXid(1, new byte[] { 1 }, new byte[] { 2, 3 });
            var equal = new OceanBaseXid(1, new byte[] { 1 }, new byte[] { 2, 3 });
            Assert.True(((IEquatable<OceanBaseXid>)xid).Equals(equal));
            Assert.True(xid.Equals((object)equal));
            Assert.True(xid.Equals(xid));
            Assert.True(equal.Equals(xid));
            Assert.Equal(xid.GetHashCode(), equal.GetHashCode());
            Assert.False(xid.Equals((OceanBaseXid?)null));
            Assert.False(xid.Equals((object?)null));
            Assert.False(xid.Equals("not an XID"));
            var different = new[]
            {
                new OceanBaseXid(0, new byte[] { 1 }, new byte[] { 2, 3 }),
                new OceanBaseXid(1, new byte[] { 1, 2 }, new byte[] { 3 }),
                new OceanBaseXid(1, new byte[] { 0 }, new byte[] { 2, 3 }),
                new OceanBaseXid(1, new byte[] { 1 }, new byte[] { 2, 4 }),
                new OceanBaseXid(1, new byte[] { 1 }, new byte[] { 2 }),
                new OceanBaseXid(1, new byte[] { 1 }, Array.Empty<byte>()),
            };
            foreach (var other in different)
            {
                Assert.False(xid.Equals(other));
                Assert.False(other.Equals(xid));
            }
            Assert.Equal(7, new HashSet<OceanBaseXid>(different) { xid, equal }.Count);
        }

        [Fact]
        public void XidAllowsAnEmptyBranchWithoutExposingItsIdentityInObjectStrings()
        {
            var bytes = Encoding.UTF8.GetBytes("secret-transaction");
            var xid = new OceanBaseXid(1234567, bytes, Array.Empty<byte>());
            Assert.Empty(xid.GetBranchQualifier());
            Assert.Equal(new OceanBaseXid(1234567, bytes, Array.Empty<byte>()), xid);
            Assert.DoesNotContain("secret-transaction", xid.ToString());
            Assert.DoesNotContain(Convert.ToHexString(bytes), xid.ToString());
            Assert.DoesNotContain("1234567", xid.ToString());
        }
    }
}
