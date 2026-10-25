namespace OceanBaseConnector.UnitTests.TestInfrastructure
{
    internal static class DiscoveryBlacklistFixture
    {
        internal static string Tns(int first, int second, int? cap = null, int timeout = 50, int threshold = 1) =>
            "APP=(DESCRIPTION=(OBLB_STRATEGY=ROTATION)" + (cap.HasValue ? "(OBLB_RETRY_ALL_DOWNS=" + cap.Value + ")" : "") +
            "(OBLB_BLACKLIST=(REMOVE_STRATEGY=(NAME=TIMEOUT)(TIMEOUT=" + timeout + "))(APPEND_STRATEGY=(NAME=" + (threshold == 1 ? "NORMAL" : "RETRYDURATION") + ")" +
            (threshold == 1 ? "" : "(RETRYTIMES=" + threshold + ")(DURATION=2147483647)") + "))" +
            "(ADDRESS_LIST=(ADDRESS=(PROTOCOL=TCP)(HOST=127.0.0.1)(PORT=" + first + ")))" +
            "(ADDRESS_LIST=(ADDRESS=(PROTOCOL=TCP)(HOST=127.0.0.1)(PORT=" + second + "))))";

        internal static string Json(int first, int second, int? cap = null, int timeout = 50, int threshold = 1) =>
            "{\"data\":{\"NET_SERVICE_NAME\":\"APP\",\"DESCRIPTION\":{\"OBLB_STRATEGY\":\"ROTATION\"," + (cap.HasValue ? "\"OBLB_RETRY_ALL_DOWNS\":" + cap.Value + "," : "") +
            "\"OBLB_BLACKLIST\":{\"REMOVE_STRATEGY\":{\"NAME\":\"TIMEOUT\",\"TIMEOUT\":" + timeout + "},\"APPEND_STRATEGY\":{\"NAME\":\"" + (threshold == 1 ? "NORMAL" : "RETRYDURATION") + "\"" +
            (threshold == 1 ? "" : ",\"RETRYTIMES\":" + threshold + ",\"DURATION\":2147483647") + "}}," +
            "\"ADDRESS_LIST\":[{\"ADDRESS\":[{\"PROTOCOL\":\"TCP\",\"HOST\":\"127.0.0.1\",\"PORT\":" + first + "}]}," +
            "{\"ADDRESS\":[{\"PROTOCOL\":\"TCP\",\"HOST\":\"127.0.0.1\",\"PORT\":" + second + "}]}]}}}";
    }
}
