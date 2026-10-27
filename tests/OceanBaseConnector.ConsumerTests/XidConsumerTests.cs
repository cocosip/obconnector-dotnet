using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    public sealed class XidConsumerTests
    {
        [Theory]
        [InlineData("netstandard2.0")]
        [InlineData("netstandard2.1")]
        [InlineData("net10.0")]
        public void EveryAssetProvidesTheImmutableXidValueContract(string target)
        {
            var context = new AssemblyLoadContext("xid-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                var type = assembly.GetType("OceanBaseConnector.OceanBaseXid", throwOnError: true)!;
                Assert.True(type.IsSealed);
                Assert.Contains(type.GetInterfaces(), i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEquatable<>));
                var formatProperty = type.GetProperty("FormatId")!;
                var globalMethod = type.GetMethod("GetGlobalTransactionId")!;
                var branchMethod = type.GetMethod("GetBranchQualifier")!;
                Assert.False(formatProperty.CanWrite);
                foreach (var format in new[] { int.MinValue, -2, 0, int.MaxValue })
                {
                    var global = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
                    var branch = Enumerable.Range(192, 64).Select(i => (byte)i).ToArray();
                    var xid = Activator.CreateInstance(type, format, global, branch)!;
                    var same = Activator.CreateInstance(type, format, global, branch)!;
                    var hash = xid.GetHashCode();
                    global[0] = 88; branch[0] = 88;
                    var returnedGlobal = (byte[])globalMethod.Invoke(xid, null)!;
                    var returnedBranch = (byte[])branchMethod.Invoke(xid, null)!;
                    Assert.Equal(0, returnedGlobal[0]); Assert.Equal(192, returnedBranch[0]);
                    returnedGlobal[0] = 99; returnedBranch[0] = 99;
                    Assert.Equal(format, formatProperty.GetValue(xid));
                    Assert.Equal(same, xid);
                    Assert.Equal(hash, same.GetHashCode());
                    Assert.Equal(0, ((byte[])globalMethod.Invoke(xid, null)!)[0]);
                    Assert.Equal(192, ((byte[])branchMethod.Invoke(xid, null)!)[0]);
                    Assert.DoesNotContain("00010203", xid.ToString());
                }
                var empty = Activator.CreateInstance(type, 1, new byte[] { 1 }, Array.Empty<byte>())!;
                Assert.Empty((byte[])branchMethod.Invoke(empty, null)!);
                foreach (var arguments in new object?[][]
                {
                    new object?[] { -1, new byte[] { 1 }, Array.Empty<byte>() },
                    new object?[] { 1, null, Array.Empty<byte>() },
                    new object?[] { 1, new byte[] { 1 }, null },
                    new object?[] { 1, Array.Empty<byte>(), Array.Empty<byte>() },
                    new object?[] { 1, new byte[65], Array.Empty<byte>() },
                    new object?[] { 1, new byte[] { 1 }, new byte[65] },
                })
                {
                    var error = Assert.Throws<TargetInvocationException>(() => Activator.CreateInstance(type, arguments));
                    Assert.IsAssignableFrom<ArgumentException>(error.InnerException);
                }
            }
            finally { context.Unload(); }
        }
    }
}
