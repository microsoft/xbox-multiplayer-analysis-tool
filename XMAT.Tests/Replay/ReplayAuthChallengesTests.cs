// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.Operations.Replay;

namespace XMAT.Tests.Replay
{
    // How a Windows-authentication run records a WWW-Authenticate or Proxy-Authenticate value (ReplayAuthChallenges): each
    // Negotiate, NTLM or Kerberos challenge keeps only its scheme name, other challenges stay as received, and anything that
    // is not a challenge list as RFC 9110 §11.6.1 has it drops the whole value, so no server blob is ever recorded.
    public class ReplayAuthChallengesTests
    {
        private const string Canary = "WEFNQVRDQU5BUlk=";

        [Theory]
        [InlineData("Negotiate", "Negotiate")]
        [InlineData("Negotiate " + Canary, "Negotiate")]
        [InlineData("NTLM TlRMTVNTUAACAAAADAAMADgAAAA=", "NTLM")]
        [InlineData("Kerberos " + Canary, "Kerberos")]
        [InlineData("negotiate " + Canary, "negotiate")]
        [InlineData("NEGOTIATE " + Canary, "NEGOTIATE")]
        [InlineData("nTlM " + Canary, "nTlM")]
        [InlineData("Negotiate YQ==", "Negotiate")]
        [InlineData("Negotiate a.b-c_d~e+f/g==", "Negotiate")]
        [InlineData("Negotiate, NTLM", "Negotiate, NTLM")]
        [InlineData("Negotiate " + Canary + ", NTLM " + Canary, "Negotiate, NTLM")]
        [InlineData("Negotiate realm=\"" + Canary + "\"", "Negotiate")]
        [InlineData("Negotiate , realm=\"" + Canary + "\", other=abc", "Negotiate")]
        [InlineData("Basic , realm=\"x\"", "Basic , realm=\"x\"")]
        [InlineData("Basic \t, realm=\"x\"", "Basic \t, realm=\"x\"")]
        [InlineData("Basic realm=\"a\", Digest realm=\"b\", Bearer realm=\"c\"", "Basic realm=\"a\", Digest realm=\"b\", Bearer realm=\"c\"")]
        [InlineData("Basic realm=\"a\", charset=\"UTF-8\", Digest realm=\"b\", charset=\"UTF-8\"",
            "Basic realm=\"a\", charset=\"UTF-8\", Digest realm=\"b\", charset=\"UTF-8\"")]
        [InlineData("Basic realm=\"a, b\", Negotiate " + Canary, "Basic realm=\"a, b\", Negotiate")]
        [InlineData("Negotiate " + Canary + ", Basic realm=\"a, b\"", "Negotiate, Basic realm=\"a, b\"")]
        [InlineData("Basic realm=\"x\", charset=\"UTF-8\"", "Basic realm=\"x\", charset=\"UTF-8\"")]
        [InlineData("Basic realm=\"x\" ,  charset=\"UTF-8\"", "Basic realm=\"x\" ,  charset=\"UTF-8\"")]
        [InlineData("Basic realm = \"x\"", "Basic realm = \"x\"")]
        [InlineData("Basic  realm=x", "Basic  realm=x")]
        [InlineData("Basic realm=\"a\\\"b, c\\\\\", NTLM", "Basic realm=\"a\\\"b, c\\\\\", NTLM")]
        [InlineData("Bearer realm=\"api\", error=\"invalid_token\"", "Bearer realm=\"api\", error=\"invalid_token\"")]
        [InlineData("Newauth realm=\"apps\", type=1, title=\"Login to \\\"apps\\\"\", Basic realm=\"simple\"",
            "Newauth realm=\"apps\", type=1, title=\"Login to \\\"apps\\\"\", Basic realm=\"simple\"")]
        [InlineData(" , Negotiate " + Canary + " , , Basic realm=\"r\" ,", "Negotiate, Basic realm=\"r\"")]
        [InlineData("Digest realm=\"r\u00E9\"", "Digest realm=\"r\u00E9\"")]
        [InlineData("Negotiate  ", "Negotiate")]
        [InlineData("Basic realm=", "Basic realm=")]
        [InlineData("Basic realm\t=\t\"x\",\tNTLM " + Canary, "Basic realm\t=\t\"x\", NTLM")]
        public void AWindowsChallenge_KeepsOnlyItsScheme_AndOthersStayAsReceived(string value, string recorded)
        {
            Assert.Equal(recorded, ReplayAuthChallenges.Sanitize(value));
        }

        // Anything that is not a challenge list exactly drops the whole value, with whatever it held.
        [Theory]
        [InlineData("")]
        [InlineData(" , , ")]
        [InlineData("realm=\"" + Canary + "\"")]
        [InlineData("realm=\"x\", Negotiate " + Canary)]
        [InlineData("Negotiate " + Canary + ", realm=\"x\"")]
        [InlineData("Basic realm=\"" + Canary)]
        [InlineData("Basic realm=\"a\\")]
        [InlineData("Basic realm=\"a\", Negotiate \"" + Canary + "\"")]
        [InlineData("Negotiate\t" + Canary)]
        [InlineData("Negotiate " + Canary + "\u0001")]
        [InlineData("Negotiate abc==x")]
        [InlineData("Negotiate =" + Canary)]
        [InlineData("Basic realm=x y")]
        [InlineData("Basic realm=\"a\u0001b\"")]
        [InlineData("Basic realm=\"\u0100\"")]
        [InlineData("N\u00E9gotiate " + Canary)]
        [InlineData("Negotiate " + Canary + "\u00E9")]
        [InlineData("=Negotiate")]
        [InlineData("Negotiate, other=" + Canary)]
        [InlineData("Negotiate , \"" + Canary + "\"")]
        [InlineData("Negotiate/" + Canary)]
        [InlineData("Basic realm=\"a\\\u0001\"")]
        [InlineData("Basic, realm=\"x\"")]
        [InlineData("Negotiate, realm=\"" + Canary + "\", other=abc")]
        [InlineData("Basic\t, realm=\"x\"")]
        [InlineData("Basic realm=\"a\", realm=\"b\"")]
        [InlineData("Basic realm=\"a\", REALM=\"b\"")]
        [InlineData("Basic , realm=\"a\", charset=\"x\", Charset=\"y\"")]
        [InlineData("Basic realm=\"a\", Negotiate realm=\"" + Canary + "\", Realm=\"" + Canary + "\"")]
        public void AnythingElse_DropsTheWholeValue(string value)
        {
            Assert.Null(ReplayAuthChallenges.Sanitize(value));
        }

        [Theory]
        [InlineData("WWW-Authenticate", true)]
        [InlineData("www-authenticate", true)]
        [InlineData("Proxy-Authenticate", true)]
        [InlineData("PROXY-AUTHENTICATE", true)]
        [InlineData("Authentication-Info", false)]
        [InlineData("Authorization", false)]
        [InlineData(null, false)]
        public void BothChallengeFields_AreRead_InAnyCasing(string name, bool challenge)
        {
            Assert.Equal(challenge, ReplayAuthChallenges.IsChallengeHeader(name));
        }
    }
}
