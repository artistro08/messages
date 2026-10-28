package libgm

// Golden vector exporter. Copied into pkg/libgm of the pinned mautrix-gmessages
// checkout by Export-Golden.ps1 and run as TestExportGolden. Each export<Area>
// writes GOLDEN_OUT/<area>.json for the C# tests to match.

import (
	"bytes"
	"context"
	"crypto/aes"
	"crypto/cipher"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha256"
	"crypto/x509"
	"encoding/hex"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/rs/zerolog"
	"go.mau.fi/util/exhttp"
	"go.mau.fi/util/pblite"
	"google.golang.org/protobuf/proto"

	"go.mau.fi/mautrix-gmessages/pkg/libgm/crypto"
	"go.mau.fi/mautrix-gmessages/pkg/libgm/events"
	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
	"go.mau.fi/mautrix-gmessages/pkg/libgm/util"
)

// capturedRequest is one outgoing HTTP request, flattened to plain values.
type capturedRequest struct {
	Method   string            `json:"method"`
	URL      string            `json:"url"`
	Headers  map[string]string `json:"headers"`
	BodyHex  string            `json:"bodyHex"`
	BodyText string            `json:"bodyText"`
}

// captureTransport records every request and answers with Respond (or an empty 200).
type captureTransport struct {
	Requests []capturedRequest
	Respond  func(*http.Request) *http.Response
}

func (ct *captureTransport) RoundTrip(req *http.Request) (*http.Response, error) {
	// Read Body
	var body []byte
	if req.Body != nil {
		body, _ = io.ReadAll(req.Body)
		_ = req.Body.Close()
	}

	// Flatten Headers (multiple values joined with ", ")
	headers := map[string]string{}
	for name, values := range req.Header {
		headers[name] = strings.Join(values, ", ")
	}
	ct.Requests = append(ct.Requests, capturedRequest{
		Method:   req.Method,
		URL:      req.URL.String(),
		Headers:  headers,
		BodyHex:  hex.EncodeToString(body),
		BodyText: string(body),
	})

	// Answer
	if ct.Respond != nil {
		resp := ct.Respond(req)
		resp.Request = req
		return resp, nil
	}
	return &http.Response{
		StatusCode: 200,
		Header:     http.Header{},
		Body:       io.NopCloser(bytes.NewReader(nil)),
		Request:    req,
	}, nil
}

func goldenSeq(start, count int) []byte {
	out := make([]byte, count)
	for i := range out {
		out[i] = byte(start + i)
	}
	return out
}

// newGoldenClient builds a Client with fixed fake credentials whose HTTP goes through the returned transport.
func newGoldenClient(t *testing.T) (*Client, *captureTransport) {
	t.Helper()

	// Fixed Refresh Key (P-256, scalar 0x01..0x20)
	scalar := goldenSeq(1, 32)
	priv, err := ecdsa.ParseRawPrivateKey(elliptic.P256(), scalar)
	if err != nil {
		t.Fatalf("refresh key: %v", err)
	}
	pub, err := priv.PublicKey.Bytes()
	if err != nil {
		t.Fatalf("refresh key public: %v", err)
	}

	// Fixed Auth Data
	device := func() *gmproto.Device {
		return &gmproto.Device{UserID: 1, SourceID: "golden-src", Network: "GDitto"}
	}
	auth := &AuthData{
		RequestCrypto:    &crypto.AESCTRHelper{AESKey: goldenSeq(0x00, 32), HMACKey: goldenSeq(0x20, 32)},
		RefreshKey:       &crypto.JWK{KeyType: "EC", Curve: "P-256", D: scalar, X: pub[1:33], Y: pub[33:]},
		Browser:          device(),
		Mobile:           device(),
		TachyonAuthToken: []byte("fake-tachyon"),
		TachyonTTL:       86400000000,
		SessionID:        uuid.MustParse("00112233-4455-6677-8899-aabbccddeeff"),
		DestRegID:        uuid.MustParse("ffeeddcc-bbaa-9988-7766-554433221100"),
		Cookies:          map[string]string{"SAPISID": "fake-sapisid", "SID": "fake-sid"},
	}

	// Client With Captured HTTP
	c := NewClient(auth, nil, zerolog.Nop(), exhttp.ClientSettings{})
	ct := &captureTransport{}
	c.http = &http.Client{Transport: ct}
	c.lphttp = &http.Client{Transport: ct}
	return c, ct
}

// writeGolden writes v as indented JSON to out/<area>.json.
func writeGolden(t *testing.T, out, area string, v any) {
	t.Helper()
	data, err := json.MarshalIndent(v, "", "  ")
	if err != nil {
		t.Fatalf("marshal %s: %v", area, err)
	}
	if err = os.MkdirAll(out, 0o755); err != nil {
		t.Fatalf("mkdir %s: %v", out, err)
	}
	if err = os.WriteFile(filepath.Join(out, area+".json"), append(data, '\n'), 0o644); err != nil {
		t.Fatalf("write %s: %v", area, err)
	}
}

// TestExportGolden runs every export; GOLDEN_OUT is the output folder.
func TestExportGolden(t *testing.T) {
	out := os.Getenv("GOLDEN_OUT")
	if out == "" {
		t.Fatal("GOLDEN_OUT is not set; run tools/golden/Export-Golden.ps1")
	}

	checkGoldenHelpers(t)
	exportSmoke(t, out)
	exportProtoBinary(t, out)
	exportCrypto(t, out)
	exportPbLite(t, out)
	exportHttp(t, out)
}

// checkGoldenHelpers sanity-checks the client and capture transport; writes no file.
func checkGoldenHelpers(t *testing.T) {
	c, ct := newGoldenClient(t)
	ct.Respond = func(*http.Request) *http.Response {
		return &http.Response{StatusCode: 200, Header: http.Header{}, Body: io.NopCloser(bytes.NewReader([]byte("[]")))}
	}
	resp, err := c.makeProtobufHTTPRequest(util.SignInGaiaURL, &gmproto.AuthMessage{RequestID: "r1"}, ContentTypePBLite)
	if err != nil {
		t.Fatalf("helper request: %v", err)
	}
	_ = resp.Body.Close()
	if len(ct.Requests) != 1 {
		t.Fatalf("expected 1 captured request, got %d", len(ct.Requests))
	}
	if ct.Requests[0].Headers["Cookie"] == "" {
		t.Fatal("captured request has no Cookie header")
	}
}

func exportSmoke(t *testing.T, out string) {
	msg := &gmproto.AuthMessage{RequestID: "r1", Network: "GDitto", ConfigVersion: util.ConfigMessage}
	data, err := pblite.Marshal(msg)
	if err != nil {
		t.Fatalf("pblite: %v", err)
	}
	writeGolden(t, out, "smoke", map[string]any{"pblite": string(data)})
}

// exportProtoBinary writes proto.json: binary protobuf of ConfigVersion and Device, as lowercase hex.
func exportProtoBinary(t *testing.T, out string) {
	// Config Version
	version, err := proto.Marshal(&gmproto.ConfigVersion{Year: 2026, Month: 9, Day: 2, V1: 4, V2: 6})
	if err != nil {
		t.Fatalf("marshal config version: %v", err)
	}

	// Device
	device, err := proto.Marshal(&gmproto.Device{UserID: 1, SourceID: "abc", Network: "GDitto"})
	if err != nil {
		t.Fatalf("marshal device: %v", err)
	}

	writeGolden(t, out, "proto", map[string]any{
		"configVersionHex": hex.EncodeToString(version),
		"deviceHex":        hex.EncodeToString(device),
	})
}

// exportCrypto writes crypto.json: AES-CTR ciphertexts, byteHash, HKDF and refresh-key (JWK) vectors.
// The JWK uses the fixed scalar 0x01..0x20 so the file is stable across regenerations; only the signature is random.
func exportCrypto(t *testing.T, out string) {
	// Aes Ctr (fixed keys, patterned plaintexts; the IV is random and sits inside each output)
	helper := &crypto.AESCTRHelper{AESKey: goldenSeq(0x00, 32), HMACKey: goldenSeq(0x20, 32)}
	var aesCases []map[string]any
	for _, n := range []int{0, 1, 16, 17, 40, 1000} {
		plain := make([]byte, n)
		for i := range plain {
			plain[i] = byte(i*7 + 3)
		}
		enc, err := helper.Encrypt(plain)
		if err != nil {
			t.Fatalf("encrypt %d: %v", n, err)
		}
		aesCases = append(aesCases, map[string]any{"plaintextHex": hex.EncodeToString(plain), "encryptedHex": hex.EncodeToString(enc)})
	}

	// Counter Carry (stdlib CTR with IVs that overflow into higher bytes; layout ct || iv || hmac)
	block, err := aes.NewCipher(helper.AESKey)
	if err != nil {
		t.Fatalf("carry cipher: %v", err)
	}
	for _, iv := range [][]byte{bytes.Repeat([]byte{0xff}, 16), append(make([]byte, 8), bytes.Repeat([]byte{0xff}, 8)...)} {
		plain := goldenSeq(0x10, 50)
		ct := make([]byte, len(plain))
		cipher.NewCTR(block, iv).XORKeyStream(ct, plain)
		ct = append(ct, iv...)
		mac := hmac.New(sha256.New, helper.HMACKey)
		mac.Write(ct)
		ct = append(ct, mac.Sum(nil)...)
		got, err := helper.Decrypt(append([]byte(nil), ct...))
		if err != nil || !bytes.Equal(got, plain) {
			t.Fatalf("carry decrypt mismatch: %v", err)
		}
		aesCases = append(aesCases, map[string]any{"plaintextHex": hex.EncodeToString(plain), "encryptedHex": hex.EncodeToString(ct)})
	}

	// Byte Hash
	var hashCases []map[string]any
	for _, in := range [][]byte{{}, {1, 2, 3}, {0xff}, encryptionKeyInfo, goldenSeq(0x80, 64)} {
		hashCases = append(hashCases, map[string]any{"inputHex": hex.EncodeToString(in), "result": byteHash(in)})
	}

	// Hkdf
	key32 := goldenSeq(0x40, 32)
	var hkdfCases []map[string]any
	for _, c := range []struct{ key, salt, info []byte }{
		{[]byte("k"), []byte("s"), []byte("i")},
		{[]byte("k"), []byte{}, []byte("i")},
		{key32, encryptionKeyInfo, []byte("client")},
		{key32, encryptionKeyInfo, []byte("server")},
	} {
		hkdfCases = append(hkdfCases, map[string]any{
			"keyHex":    hex.EncodeToString(c.key),
			"saltHex":   hex.EncodeToString(c.salt),
			"infoHex":   hex.EncodeToString(c.info),
			"resultHex": hex.EncodeToString(doHKDF(c.key, c.salt, c.info)),
		})
	}

	// Jwk (fixed scalar 0x01..0x20)
	priv, err := ecdsa.ParseRawPrivateKey(elliptic.P256(), goldenSeq(1, 32))
	if err != nil {
		t.Fatalf("jwk key: %v", err)
	}
	pubBytes, err := priv.PublicKey.Bytes()
	if err != nil {
		t.Fatalf("jwk public: %v", err)
	}
	jwk := &crypto.JWK{KeyType: "EC", Curve: "P-256", D: goldenSeq(1, 32), X: pubBytes[1:33], Y: pubBytes[33:]}
	pub, err := jwk.GetPublicKey()
	if err != nil {
		t.Fatalf("jwk get public: %v", err)
	}
	spki, err := x509.MarshalPKIXPublicKey(pub)
	if err != nil {
		t.Fatalf("spki: %v", err)
	}
	signed := []byte("req-1:1700000000000000")
	digest := sha256.Sum256(signed)
	signing, err := jwk.GetPrivateKey()
	if err != nil {
		t.Fatalf("jwk get private: %v", err)
	}
	sig, err := ecdsa.SignASN1(rand.Reader, signing, digest[:])
	if err != nil {
		t.Fatalf("sign: %v", err)
	}

	writeGolden(t, out, "crypto", map[string]any{
		"aesctr": map[string]any{
			"aesKeyHex":  hex.EncodeToString(helper.AESKey),
			"hmacKeyHex": hex.EncodeToString(helper.HMACKey),
			"cases":      aesCases,
		},
		"byteHash": hashCases,
		"hkdf":     hkdfCases,
		"jwk": map[string]any{
			"dHex":         hex.EncodeToString(jwk.D),
			"xHex":         hex.EncodeToString(jwk.X),
			"yHex":         hex.EncodeToString(jwk.Y),
			"spkiHex":      hex.EncodeToString(spki),
			"signedText":   string(signed),
			"signatureHex": hex.EncodeToString(sig),
		},
	})
}

// pbliteMessage builds an empty gmproto message for a proto full name (decode cases).
func pbliteMessage(t *testing.T, name string) proto.Message {
	switch name {
	case "authentication.Device":
		return &gmproto.Device{}
	case "authentication.ConfigVersion":
		return &gmproto.ConfigVersion{}
	case "conversations.Conversation":
		return &gmproto.Conversation{}
	case "authentication.AuthMessage":
		return &gmproto.AuthMessage{}
	}
	t.Fatalf("unknown pblite type %s", name)
	return nil
}

// exportPbLite writes pblite.json: encode cases (binary + PBLite from real messages) and
// decode cases (hand-written lenient PBLite inputs + Go's binary result).
func exportPbLite(t *testing.T, out string) {
	// Shared Pieces
	auth := &gmproto.AuthMessage{
		RequestID:        "req-1",
		Network:          "GDitto",
		TachyonAuthToken: []byte{1, 2, 3, 0xfe, 0xff},
		ConfigVersion:    util.ConfigMessage,
	}
	device := &gmproto.Device{UserID: 1, SourceID: "golden-src", Network: "GDitto"}

	// Encode Cases
	encodeMessages := []proto.Message{
		// Every Field Set
		auth,
		// pblite_binary Message Field
		&gmproto.SignInGaiaRequest{
			AuthMessage: &gmproto.AuthMessage{RequestID: "r2", Network: "Bugle", ConfigVersion: util.ConfigMessage},
			Inner: &gmproto.SignInGaiaRequest_Inner{
				DeviceID: &gmproto.SignInGaiaRequest_Inner_DeviceID{UnknownInt1: 3, DeviceID: "messages-web-00112233445566778899aabbccddeeff"},
				SomeData: &gmproto.SignInGaiaRequest_Inner_Data{SomeData: goldenSeq(0x10, 33)},
			},
			Network: "Bugle",
		},
		// Repeated pblite_binary String
		&gmproto.OutgoingRPCMessage{
			Mobile: device,
			Data: &gmproto.OutgoingRPCMessage_Data{
				RequestID:   "rpc-1",
				BugleRoute:  gmproto.BugleRoute_DataEvent,
				MessageData: goldenSeq(0, 20),
				MessageTypeData: &gmproto.OutgoingRPCMessage_Data_Type{
					EmptyArr:    &gmproto.EmptyArr{},
					MessageType: gmproto.MessageType_BUGLE_MESSAGE,
				},
			},
			Auth:                &gmproto.OutgoingRPCMessage_Auth{RequestID: "rpc-1", TachyonAuthToken: []byte("fake-tachyon"), ConfigVersion: util.ConfigMessage},
			TTL:                 86400000000,
			DestRegistrationIDs: []string{"ffeeddcc-bbaa-9988-7766-554433221100", "00112233-4455-6677-8899-aabbccddeeff"},
		},
		// Repeated Message
		&gmproto.AckMessageRequest{
			AuthData: auth,
			EmptyArr: &gmproto.EmptyArr{},
			Acks: []*gmproto.AckMessageRequest_Message{
				{RequestID: "ack-1", Device: device},
				{RequestID: "ack-2", Device: device},
			},
		},
		// As doLongPoll Builds It (Nested Empty Messages)
		&gmproto.ReceiveMessagesRequest{
			Auth: auth,
			Unknown: &gmproto.ReceiveMessagesRequest_UnknownEmptyObject2{
				Unknown: &gmproto.ReceiveMessagesRequest_UnknownEmptyObject1{},
			},
		},
		// Optional Message With MoreParameters Nil
		&gmproto.RegisterRefreshRequest{
			MessageAuth:       auth,
			CurrBrowserDevice: device,
			UnixTimestamp:     1700000000000000,
			Signature:         goldenSeq(0x30, 70),
			Parameters:        &gmproto.RegisterRefreshRequest_Parameters{EmptyArr: &gmproto.EmptyArr{}},
			MessageType:       2,
		},
		// Optional Message With MoreParameters Set (Sparse Index 102)
		&gmproto.RegisterRefreshRequest{
			MessageAuth: auth,
			Parameters: &gmproto.RegisterRefreshRequest_Parameters{
				EmptyArr: &gmproto.EmptyArr{},
				MoreParameters: &gmproto.RegisterRefreshRequest_MoreParameters{
					Three:   3,
					PushReg: &gmproto.RegisterRefreshRequest_PushRegistration{Type: "messages_web", Url: "https://push.example/x", P256Dh: "p", Auth: "a"},
				},
			},
		},
		// Int64 Above 2^53
		&gmproto.Device{UserID: 9007199254740993, SourceID: "big", Network: "GDitto"},
		// Uint64 Above 2^53
		&gmproto.SignInGaiaResponse_Header{UnknownInt2: 18446744073709551615, UnknownTimestamp: -1700000000000000},
		// Enum And Bool Set (Plus Repeated Message And Repeated String)
		&gmproto.Conversation{
			ConversationID:    "c1",
			Unread:            true,
			Status:            gmproto.ConversationStatus_ARCHIVED,
			Pinned:            true,
			OtherParticipants: []string{"p1", "p2"},
			Participants:      []*gmproto.Participant{{FullName: "A", IsMe: true}},
		},
		// JSON String Escaping (HTML, quotes, control chars, U+2028, non-BMP)
		&gmproto.Device{UserID: 2, SourceID: "a<b>&\"\\n\t é\U0001F600\x01", Network: "n"},
		// Optional Fields At Their Zero Value (still set)
		&gmproto.GetOrCreateConversationRequest{CreateRCSGroup: proto.Bool(false), RCSGroupName: proto.String("")},
		// Oneof Member Set To Its Zero Value (still set)
		&gmproto.UpdateConversationData{ConversationID: "c9", Data: &gmproto.UpdateConversationData_Mute{Mute: gmproto.ConversationMuteStatus_UNMUTE}},
		// Every Field At Its Zero Value
		&gmproto.Device{},
		&gmproto.AuthMessage{},
	}
	var encodeCases []map[string]any
	for _, m := range encodeMessages {
		binary, err := proto.Marshal(m)
		if err != nil {
			t.Fatalf("pblite binary: %v", err)
		}
		text, err := pblite.Marshal(m)
		if err != nil {
			t.Fatalf("pblite marshal: %v", err)
		}
		encodeCases = append(encodeCases, map[string]any{
			"type":      string(m.ProtoReflect().Descriptor().FullName()),
			"binaryHex": hex.EncodeToString(binary),
			"pblite":    string(text),
		})
	}

	// Decode Cases (leniency)
	var decodeCases []map[string]any
	for _, c := range []struct{ typ, input string }{
		{"authentication.Device", `["9007199254740993","src","net"]`},                            // int as string
		{"authentication.Device", `["12","src","net"]`},                                          // small int as string
		{"conversations.Conversation", `["c1",null,null,null,null,1,null,null,null,1]`},          // bool as 1
		{"conversations.Conversation", `["c1",null,null,null,null,0]`},                           // bool as 0
		{"authentication.ConfigVersion", `[null,null,2026,9,2,null,4,null,6,{"4":11}]`},          // override object
		{"authentication.ConfigVersion", `[null,null,2026,9,2,null,4,null,6,{"10":1,"3":2025}]`}, // override plus unknown key
		{"authentication.Device", `["1","src","net",null,null,null,null]`},                       // extra trailing nulls
		{"conversations.Conversation", `["c1","name"]`},                                          // shorter than max field
		{"authentication.Device", `["1","src","net","x",5,[1,2],{"a":1}]`},                       // unknown extra slots
		{"authentication.AuthMessage", `[]`},                                                     // empty array
	} {
		msg := pbliteMessage(t, c.typ)
		if err := pblite.Unmarshal([]byte(c.input), msg); err != nil {
			t.Fatalf("pblite unmarshal %s: %v", c.input, err)
		}
		binary, err := proto.Marshal(msg)
		if err != nil {
			t.Fatalf("decode binary: %v", err)
		}
		decodeCases = append(decodeCases, map[string]any{"type": c.typ, "input": c.input, "binaryHex": hex.EncodeToString(binary)})
	}

	writeGolden(t, out, "pblite", map[string]any{"encode": encodeCases, "decode": decodeCases})
}

// httpResponse builds a canned response with the given status, content type and body.
func httpResponse(status int, contentType string, body []byte, extra http.Header) *http.Response {
	h := http.Header{}
	if contentType != "" {
		h.Set("Content-Type", contentType)
	}
	for name, values := range extra {
		for _, v := range values {
			h.Add(name, v)
		}
	}
	return &http.Response{StatusCode: status, Header: h, Body: io.NopCloser(bytes.NewReader(body))}
}

// exportHttp writes http.json: constants, paths, captured relay and config requests, SAPISIDHASH
// outputs, Set-Cookie handling, 5xx retry behavior and decoded error bodies.
func exportHttp(t *testing.T, out string) {
	pbliteString := func(m proto.Message) string {
		b, err := pblite.Marshal(m)
		if err != nil {
			t.Fatalf("pblite marshal: %v", err)
		}
		return string(b)
	}

	// Constants
	constants := map[string]any{
		"googleApiKey":         util.GoogleAPIKey,
		"userAgent":            util.UserAgent,
		"secUA":                util.SecUA,
		"uaPlatform":           util.UAPlatform,
		"xUserAgent":           util.XUserAgent,
		"qrCodeUrlBase":        util.QRCodeURLBase,
		"secUAMobile":          util.SecUAMobile,
		"qrNetwork":            util.QRNetwork,
		"googleNetwork":        util.GoogleNetwork,
		"configMessagePblite":  pbliteString(util.ConfigMessage),
		"browserDetailsPblite": pbliteString(util.BrowserDetailsMessage),
	}

	// Paths
	paths := map[string]string{
		"messagesBase":          util.MessagesBaseURL,
		"googleAuthentication":  util.GoogleAuthenticationURL,
		"googleTimesource":      util.GoogleTimesourceURL,
		"uploadMedia":           util.UploadMediaURL,
		"registerPhoneRelay":    util.RegisterPhoneRelayURL,
		"refreshPhoneRelay":     util.RefreshPhoneRelayURL,
		"getWebEncryptionKey":   util.GetWebEncryptionKeyURL,
		"revokeRelayPairing":    util.RevokeRelayPairingURL,
		"receiveMessages":       util.ReceiveMessagesURL,
		"sendMessage":           util.SendMessageURL,
		"ackMessages":           util.AckMessagesURL,
		"receiveMessagesGoogle": util.ReceiveMessagesURLGoogle,
		"sendMessageGoogle":     util.SendMessageURLGoogle,
		"ackMessagesGoogle":     util.AckMessagesURLGoogle,
		"signInGaia":            util.SignInGaiaURL,
		"registerRefresh":       util.RegisterRefreshURL,
		"config":                util.ConfigURL,
	}

	// Relay Request
	c, ct := newGoldenClient(t)
	resp, err := c.makeProtobufHTTPRequest(util.SignInGaiaURL, &gmproto.AuthMessage{RequestID: "r1"}, ContentTypePBLite)
	if err != nil {
		t.Fatalf("relay request: %v", err)
	}
	_ = resp.Body.Close()
	relayRequest := ct.Requests[0]

	// Config Request
	c, ct = newGoldenClient(t)
	configMsg := &gmproto.Config{ClientVersion: "20260902_00_RC00", CountryCode: "US"}
	ct.Respond = func(*http.Request) *http.Response {
		return httpResponse(200, ContentTypePBLite, []byte(pbliteString(configMsg)), nil)
	}
	if _, err = c.fetchConfig(context.Background()); err != nil {
		t.Fatalf("fetch config: %v", err)
	}
	configRequest := ct.Requests[0]

	// SAPISIDHASH (Time-Based; Spread Across Seconds)
	sapisid := []string{}
	for i := 0; i < 3; i++ {
		if i > 0 {
			time.Sleep(1100 * time.Millisecond)
		}
		sapisid = append(sapisid, SAPISIDHash("https://messages.google.com", "fake-sapisid"))
	}

	// Set-Cookie
	c, ct = newGoldenClient(t)
	ct.Respond = func(*http.Request) *http.Response {
		return httpResponse(200, "", nil, http.Header{"Set-Cookie": {"SIDCC=new; Path=/", "SID=rotated"}})
	}
	resp, err = c.makeProtobufHTTPRequest(util.SignInGaiaURL, &gmproto.AuthMessage{RequestID: "r1"}, ContentTypePBLite)
	if err != nil {
		t.Fatalf("set-cookie request: %v", err)
	}
	_ = resp.Body.Close()
	setCookie := map[string]any{"cookiesAfter": c.AuthData.Cookies}

	// Set-Cookie With Nil Cookie Map (Stays Nil, No Cookie Or Authorization Header)
	c, ct = newGoldenClient(t)
	c.AuthData.Cookies = nil
	ct.Respond = func(*http.Request) *http.Response {
		return httpResponse(200, "", nil, http.Header{"Set-Cookie": {"SIDCC=new; Path=/"}})
	}
	resp, err = c.makeProtobufHTTPRequest(util.SignInGaiaURL, &gmproto.AuthMessage{RequestID: "r1"}, ContentTypePBLite)
	if err != nil {
		t.Fatalf("nil cookie request: %v", err)
	}
	_ = resp.Body.Close()
	setCookie["nilCookiesStayNil"] = c.AuthData.Cookies == nil
	setCookie["nilCookiesRequestHeaders"] = ct.Requests[0].Headers

	// Retry
	runRetry := func(statuses []int, longPoll bool) map[string]any {
		c, ct := newGoldenClient(t)
		n := 0
		ct.Respond = func(*http.Request) *http.Response {
			status := statuses[len(statuses)-1]
			if n < len(statuses) {
				status = statuses[n]
			}
			n++
			return httpResponse(status, "", nil, nil)
		}
		start := time.Now()
		resp, err := c.makeProtobufHTTPRequestContext(context.Background(), util.SignInGaiaURL, &gmproto.AuthMessage{RequestID: "r1"}, ContentTypePBLite, longPoll)
		if err != nil {
			t.Fatalf("retry request: %v", err)
		}
		_ = resp.Body.Close()
		return map[string]any{
			"statuses":    statuses,
			"longPoll":    longPoll,
			"attempts":    len(ct.Requests),
			"finalStatus": resp.StatusCode,
			"elapsedMs":   time.Since(start).Milliseconds(),
		}
	}
	retry := []map[string]any{
		runRetry([]int{503, 503, 200}, false),
		runRetry([]int{503, 503, 503}, false),
		runRetry([]int{401}, false),
		runRetry([]int{503}, true),
	}

	// Error Bodies
	errMsg := &gmproto.ErrorResponse{
		Type:    5,
		Message: "Requested entity was not found.",
		Class:   []*gmproto.ErrorResponse_ErrorClass{{Class: "type.googleapis.com/google.internal.communications.instantmessaging.v1.TachyonError"}},
	}
	errBody := []byte(pbliteString(errMsg))
	errBinary, _ := proto.Marshal(errMsg)
	errCase := func(name string, status int, contentType string, body []byte) map[string]any {
		r := httpResponse(status, contentType, body, nil)
		r.Request, _ = http.NewRequest(http.MethodGet, util.ConfigURL, nil)
		_, err := typedHTTPResponse[*gmproto.Config](r, nil)
		result := map[string]any{
			"name":        name,
			"status":      status,
			"contentType": contentType,
			"bodyHex":     hex.EncodeToString(body),
		}
		var reqErr events.RequestError
		if errors.As(err, &reqErr) {
			classes := []string{}
			for _, cl := range reqErr.Data.Class {
				classes = append(classes, cl.Class)
			}
			result["decoded"] = true
			result["type"] = reqErr.Data.Type
			result["message"] = reqErr.Data.Message
			result["classes"] = classes
		} else {
			result["decoded"] = false
		}
		return result
	}
	errorBody := []map[string]any{
		errCase("pblite", 400, ContentTypePBLite, errBody),
		errCase("pbliteWithCharset", 400, "application/json+protobuf; charset=UTF-8", errBody),
		errCase("textPlain", 400, "text/plain", errBody),
		errCase("protobuf", 400, ContentTypeProtobuf, errBinary),
		errCase("garbage", 500, "text/html", []byte("<html>oops</html>")),
		errCase("emptyMessage", 400, ContentTypePBLite, []byte("[]")),
	}

	writeGolden(t, out, "http", map[string]any{
		"constants":     constants,
		"paths":         paths,
		"relayRequest":  relayRequest,
		"configRequest": configRequest,
		"sapisid":       sapisid,
		"setCookie":     setCookie,
		"retry":         retry,
		"errorBody":     errorBody,
	})
}
