namespace OceanBaseConnector.UnitTests.TestInfrastructure
{
    internal static class GroupBlacklistFixture
    {
        internal static string Tns(int first, int second, int? cap = 3, int threshold = 2, int timeout = 50, bool global = true)
            => "APP=(DESCRIPTION=(OBLB_STRATEGY=ROTATION)" + (cap.HasValue ? "(OBLB_RETRY_ALL_DOWNS=" + cap + ")" : "") +
            (global ? Policy(1, 50) : "") + "(ADDRESS_LIST=" + Policy(threshold, timeout) + Address(first) + ")(ADDRESS_LIST=" + Address(second) + "))";

        internal static string Json(int first, int second, int? cap = 3, int threshold = 2, int timeout = 50, bool global = true)
            => "{\"data\":{\"NET_SERVICE_NAME\":\"APP\",\"DESCRIPTION\":{\"OBLB_STRATEGY\":\"ROTATION\"," +
            (cap.HasValue ? "\"OBLB_RETRY_ALL_DOWNS\":" + cap + "," : "") + (global ? JsonPolicy(1, 50) + "," : "") +
            "\"ADDRESS_LIST\":[{" + JsonPolicy(threshold, timeout) + ",\"ADDRESS\":[" + JsonAddress(first) + "]},{\"ADDRESS\":[" + JsonAddress(second) + "]}]}}}";

        private static string Policy(int threshold, int timeout) => "(OBLB_BLACKLIST=(REMOVE_STRATEGY=(NAME=TIMEOUT)(TIMEOUT=" + timeout + "))" +
            "(APPEND_STRATEGY=(NAME=" + (threshold == 1 ? "NORMAL" : "RETRYDURATION") + ")" +
            (threshold == 1 ? "" : "(RETRYTIMES=" + threshold + ")(DURATION=2147483647)") + "))";
        private static string JsonPolicy(int threshold, int timeout) => "\"OBLB_BLACKLIST\":{\"REMOVE_STRATEGY\":{\"NAME\":\"TIMEOUT\",\"TIMEOUT\":" + timeout + "}," +
            "\"APPEND_STRATEGY\":{\"NAME\":\"" + (threshold == 1 ? "NORMAL" : "RETRYDURATION") + "\"" +
            (threshold == 1 ? "" : ",\"RETRYTIMES\":" + threshold + ",\"DURATION\":2147483647") + "}}";
        private static string Address(int port) => "(ADDRESS=(PROTOCOL=TCP)(HOST=127.0.0.1)(PORT=" + port + "))";
        private static string JsonAddress(int port) => "{\"PROTOCOL\":\"TCP\",\"HOST\":\"127.0.0.1\",\"PORT\":" + port + "}";
    }
}
