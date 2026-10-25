using System;

namespace OceanBaseConnector.UnitTests.TestInfrastructure
{
    internal static class DiscoveryPolicyFixture
    {
        internal static string Tns(int first, int second, bool weighted) =>
            "APP=(DESCRIPTION=(OBLB_GROUP_STRATEGY=ROTATION)(OBLB_STRATEGY=" + (weighted ? "SERVERAFFINITY" : "ROTATION") + ")" +
            "(ADDRESS_LIST=" + Address(first, weighted ? 0 : (int?)null) + (weighted ? Address(second, 10) : "") + ")" +
            "(ADDRESS_LIST=" + Address(second, weighted ? 1 : (int?)null) + "))";

        internal static string Json(int first, int second, bool weighted) =>
            "{\"data\":{\"NET_SERVICE_NAME\":\"APP\",\"DESCRIPTION\":{\"OBLB_GROUP_STRATEGY\":\"ROTATION\",\"OBLB_STRATEGY\":\"" + (weighted ? "SERVERAFFINITY" : "ROTATION") + "\",\"ADDRESS_LIST\":[" +
            "{\"ADDRESS\":[" + JsonAddress(first, weighted ? 0 : (int?)null) + (weighted ? "," + JsonAddress(second, 10) : "") + "]}," +
            "{\"ADDRESS\":[" + JsonAddress(second, weighted ? 1 : (int?)null) + "]}]}}}";

        private static string Address(int port, int? weight) => "(ADDRESS=(PROTOCOL=TCP)(HOST=127.0.0.1)(PORT=" + port + ")" + (weight.HasValue ? "(WEIGHT=" + weight.Value + ")" : "") + ")";
        private static string JsonAddress(int port, int? weight) => "{\"PROTOCOL\":\"TCP\",\"HOST\":\"127.0.0.1\",\"PORT\":" + port + (weight.HasValue ? ",\"WEIGHT\":" + weight.Value : "") + "}";
    }
}
