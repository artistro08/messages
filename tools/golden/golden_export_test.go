package libgm

// Golden vector exporter. Copied into pkg/libgm of the pinned mautrix-gmessages
// checkout by Export-Golden.ps1 and run as TestExportGolden. Each export<Area>
// writes GOLDEN_OUT/<area>.json for the C# tests to match.

import (
	"bytes"
	"context"
	"crypto/aes"
	"crypto/cipher"
	"crypto/ecdh"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha256"
	"crypto/x509"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
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
	exportAuthData(t, out)
	exportRpc(t, out)
	exportLongPollStream(t, out)
	exportDispatch(t, out)
	exportRefresh(t, out)
	exportLongPollRequest(t, out)
	exportSignIn(t, out)
	exportPairing(t, out)
	exportConnect(t, out)
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

// exportAuthData writes authdata.json: the fixed AuthData after ClearPairing and CopyForRepair, plus the
// IsGoogleAccount / AuthNetwork / HasCookies results with and without DestRegID.
func exportAuthData(t *testing.T, out string) {
	// Flatten One AuthData To Plain Values
	flat := func(ad *AuthData) map[string]any {
		return map[string]any{
			"hasBrowser":          ad.Browser != nil,
			"hasMobile":           ad.Mobile != nil,
			"tachyonTokenHex":     hex.EncodeToString(ad.TachyonAuthToken),
			"tachyonExpiryZero":   ad.TachyonExpiry.IsZero(),
			"tachyonTTL":          ad.TachyonTTL,
			"pairingIdHex":        hex.EncodeToString(ad.PairingID[:]),
			"sessionIdHex":        hex.EncodeToString(ad.SessionID[:]),
			"destRegIdHex":        hex.EncodeToString(ad.DestRegID[:]),
			"destPhoneId":         ad.DestPhoneID,
			"cookies":             ad.Cookies,
			"cookiesNil":          ad.Cookies == nil,
			"requestCryptoAesHex": hex.EncodeToString(ad.RequestCrypto.AESKey),
			"hasRefreshKey":       ad.RefreshKey != nil,
		}
	}

	// Cleared Pairing
	c, _ := newGoldenClient(t)
	ad := c.AuthData
	ad.DestPhoneID = 42
	ad.PairingID = uuid.MustParse("01020304-0506-0708-090a-0b0c0d0e0f10")
	ad.TachyonExpiry = time.Unix(1700000000, 0)
	ad.WebEncryptionKey = []byte{1, 2, 3}
	repaired := ad.CopyForRepair()
	ad.ClearPairing()

	// Network Cases
	network := func(ad *AuthData) map[string]any {
		return map[string]any{"isGoogleAccount": ad.IsGoogleAccount(), "authNetwork": ad.AuthNetwork(), "hasCookies": ad.HasCookies()}
	}
	noReg := &AuthData{}
	withReg := &AuthData{DestRegID: uuid.MustParse("ffeeddcc-bbaa-9988-7766-554433221100")}
	withRegCookies := &AuthData{DestRegID: withReg.DestRegID, Cookies: map[string]string{}}

	writeGolden(t, out, "authdata", map[string]any{
		"cleared":  flat(ad),
		"repaired": flat(repaired),
		"network": map[string]any{
			"withoutDestRegId":          network(noReg),
			"withDestRegIdNoCookies":    network(withReg),
			"withDestRegIdEmptyCookies": network(withRegCookies),
		},
	})
}

// exportRpc writes rpc.json: buildMessage output, the SendMessage URL choice, the AckMessages request,
// Go-encrypted incoming messages with their decrypt results, and the responseType table.
func exportRpc(t *testing.T, out string) {
	pbliteString := func(m proto.Message) string {
		b, err := pblite.Marshal(m)
		if err != nil {
			t.Fatalf("pblite marshal: %v", err)
		}
		return string(b)
	}
	okResponse := func(*http.Request) *http.Response {
		return httpResponse(200, "application/json+protobuf", []byte("[]"), nil)
	}
	data := &gmproto.ListConversationsRequest{Count: 25}

	// Build
	c, _ := newGoldenClient(t)
	c.sessionHandler.sessionID = "golden-session"
	buildCases := []struct {
		name   string
		params SendMessageParams
	}{
		{"dontEncrypt", SendMessageParams{Action: gmproto.ActionType_LIST_CONVERSATIONS, Data: data, RequestID: "golden-req-1", DontEncrypt: true}},
		{"encrypted", SendMessageParams{Action: gmproto.ActionType_LIST_CONVERSATIONS, Data: data, RequestID: "golden-req-2"}},
		{"customTtl", SendMessageParams{Action: gmproto.ActionType_GET_UPDATES, RequestID: "golden-req-3", CustomTTL: 123456789}},
		{"omitTtl", SendMessageParams{Action: gmproto.ActionType_GET_UPDATES, RequestID: "golden-req-4", OmitTTL: true}},
		{"gaia2", SendMessageParams{Action: gmproto.ActionType_CREATE_GAIA_PAIRING_CLIENT_INIT, Data: data, RequestID: "golden-req-5", DontEncrypt: true, MessageType: gmproto.MessageType_GAIA_2}},
	}
	build := map[string]any{}
	for _, bc := range buildCases {
		requestID, payload, err := c.sessionHandler.buildMessage(bc.params)
		if err != nil {
			t.Fatalf("buildMessage %s: %v", bc.name, err)
		}
		build[bc.name] = map[string]any{"requestId": requestID, "pblite": pbliteString(payload)}
	}

	// Send URL
	sendUrl := map[string]any{}
	urlCases := []struct {
		name  string
		tweak func(*Client)
	}{
		{"withCookies", func(*Client) {}},
		{"googleNoCookies", func(c *Client) { c.AuthData.Cookies = nil }},
		{"nonGoogle", func(c *Client) { c.AuthData.DestRegID = uuid.Nil; c.AuthData.Cookies = nil }},
	}
	for _, uc := range urlCases {
		uc2, ct := newGoldenClient(t)
		ct.Respond = okResponse
		uc.tweak(uc2)
		if err := uc2.sessionHandler.sendMessageNoResponse(context.Background(), SendMessageParams{Action: gmproto.ActionType_GET_UPDATES, RequestID: "golden-req-6", DontEncrypt: true}); err != nil {
			t.Fatalf("sendMessageNoResponse %s: %v", uc.name, err)
		}
		sendUrl[uc.name] = ct.Requests[0].URL
	}

	// Acks
	c, ct := newGoldenClient(t)
	ct.Respond = okResponse
	for _, id := range []string{"a1", "a2", "a1"} {
		c.sessionHandler.queueMessageAck(id)
	}
	c.sessionHandler.sendAckRequest()
	acks := ct.Requests[0]

	// Decode
	c, _ = newGoldenClient(t)
	decodeCases := []struct {
		action  gmproto.ActionType
		message proto.Message
	}{
		{gmproto.ActionType_GET_UPDATES, &gmproto.UpdateEvents{Event: &gmproto.UpdateEvents_TypingEvent{TypingEvent: &gmproto.TypingEvent{}}}},
		{gmproto.ActionType_LIST_CONVERSATIONS, &gmproto.ListConversationsResponse{Conversations: []*gmproto.Conversation{{ConversationID: "c1", Name: "Golden"}}}},
		{gmproto.ActionType_NOTIFY_DITTO_ACTIVITY, &gmproto.NotifyDittoActivityResponse{}},
	}
	var decode []map[string]any
	for i, dc := range decodeCases {
		plain, err := proto.Marshal(dc.message)
		if err != nil {
			t.Fatalf("marshal decode case: %v", err)
		}
		encrypted, err := c.AuthData.RequestCrypto.Encrypt(plain)
		if err != nil {
			t.Fatalf("encrypt decode case: %v", err)
		}
		inner, err := proto.Marshal(&gmproto.RPCMessageData{SessionID: "golden-req-" + string(rune('a'+i)), Action: dc.action, EncryptedData: encrypted})
		if err != nil {
			t.Fatalf("marshal inner: %v", err)
		}
		raw := &gmproto.IncomingRPCMessage{ResponseID: "golden-resp-" + string(rune('a'+i)), BugleRoute: gmproto.BugleRoute_DataEvent, MessageData: inner}
		res, err := c.decryptInternalMessage(raw)
		if err != nil {
			t.Fatalf("decryptInternalMessage: %v", err)
		}
		decode = append(decode, map[string]any{
			"action":           dc.action.String(),
			"pblite":           pbliteString(raw),
			"decryptedDataHex": hex.EncodeToString(res.DecryptedData),
			"messageName":      string(res.DecryptedMessage.ProtoReflect().Descriptor().FullName()),
		})
	}

	// Response Table
	table := map[string]string{}
	for action, msg := range responseType {
		table[action.String()] = string(msg.ProtoReflect().Descriptor().FullName())
	}

	writeGolden(t, out, "rpc", map[string]any{
		"build":         build,
		"sendUrl":       sendUrl,
		"acks":          map[string]any{"url": acks.URL, "bodyHex": acks.BodyHex, "bodyText": acks.BodyText},
		"decode":        decode,
		"responseTable": table,
	})
}

// chunkReader returns exactly one chunk per Read call, which is the read pattern readLongPoll assumes.
type chunkReader struct {
	chunks [][]byte
}

func (cr *chunkReader) Read(p []byte) (int, error) {
	if len(cr.chunks) == 0 {
		return 0, io.EOF
	}
	n := copy(p, cr.chunks[0])
	cr.chunks = cr.chunks[1:]
	return n, nil
}

func (cr *chunkReader) Close() error { return nil }

// exportLongPollStream writes longpoll-stream.json: a full stream body fed through readLongPoll one payload
// per read, plus the payload sequence Go decoded from it.
func exportLongPollStream(t *testing.T, out string) {
	payloads := []*gmproto.LongPollingPayload{
		{Data: &gmproto.IncomingRPCMessage{ResponseID: "golden-resp-1", BugleRoute: gmproto.BugleRoute_DataEvent, MessageData: []byte("not-encrypted")}},
		{Ack: &gmproto.StartAckMessage{Count: proto.Int32(2)}},
		{Heartbeat: &gmproto.EmptyArr{}},
		{StartRead: &gmproto.EmptyArr{}},
	}

	// Chunks (leading comma for every payload but the first)
	chunks := [][]byte{[]byte("[[")}
	var body strings.Builder
	body.WriteString("[[")
	for i, p := range payloads {
		raw, err := pblite.Marshal(p)
		if err != nil {
			t.Fatalf("pblite marshal payload: %v", err)
		}
		if i > 0 {
			raw = append([]byte(","), raw...)
		}
		chunks = append(chunks, raw)
		body.Write(raw)
	}
	chunks = append(chunks, []byte("]]"))
	body.WriteString("]]")

	// Record What Go Decodes From Each Chunk
	var sequence []map[string]any
	for _, chunk := range chunks[1 : len(chunks)-1] {
		msg := &gmproto.LongPollingPayload{}
		if err := pblite.Unmarshal(bytes.TrimPrefix(chunk, []byte(",")), msg); err != nil {
			t.Fatalf("pblite unmarshal chunk: %v", err)
		}
		switch {
		case msg.GetData() != nil:
			sequence = append(sequence, map[string]any{"kind": "data", "responseId": msg.GetData().GetResponseID()})
		case msg.GetAck() != nil:
			sequence = append(sequence, map[string]any{"kind": "ack", "count": msg.GetAck().GetCount()})
		case msg.GetHeartbeat() != nil:
			sequence = append(sequence, map[string]any{"kind": "heartbeat"})
		case msg.GetStartRead() != nil:
			sequence = append(sequence, map[string]any{"kind": "startRead"})
		default:
			t.Fatalf("unknown payload kind")
		}
	}

	// Run Go's Reader And Confirm It Accepted The Ack
	c, _ := newGoldenClient(t)
	log := zerolog.Nop()
	c.readLongPoll(&log, &chunkReader{chunks: chunks}, false, func() {})
	if c.skipCount != 2 {
		t.Fatalf("readLongPoll skipCount = %d, want 2", c.skipCount)
	}

	chunkStrings := make([]string, len(chunks))
	for i, ch := range chunks {
		chunkStrings[i] = string(ch)
	}
	writeGolden(t, out, "longpoll-stream", map[string]any{
		"body":     body.String(),
		"chunks":   chunkStrings,
		"sequence": sequence,
	})
}

// goldenEventName maps a Go event value to the C# event record name.
func goldenEventName(t *testing.T, evt any) (name string, isOld bool, id string) {
	switch e := evt.(type) {
	case *WrappedMessage:
		return "MessageReceived", e.IsOld, e.GetMessageID()
	case *gmproto.Conversation:
		return "ConversationUpdated", false, e.GetConversationID()
	case *gmproto.TypingData:
		return "TypingChanged", false, e.GetConversationID()
	case *gmproto.Settings:
		return "SettingsChanged", false, ""
	case *gmproto.UserAlertEvent:
		return "UserAlert", false, e.GetAlertType().String()
	case *events.AccountChange:
		return "AccountChanged", e.IsFake, e.GetAccount()
	case *events.GaiaLoggedOut:
		return "GaiaLoggedOut", false, ""
	case *events.AuthTokenRefreshed:
		return "AuthTokenRefreshed", false, ""
	}
	t.Fatalf("unexpected event type %T", evt)
	return "", false, ""
}

// exportDispatch writes dispatch.json: Go-encrypted incoming messages fed through HandleRPCMsg with a skip
// count, plus the events Go raised (in order), the final skip count and the ack batches it sent.
func exportDispatch(t *testing.T, out string) {
	c, ct := newGoldenClient(t)
	ct.Respond = func(*http.Request) *http.Response {
		return httpResponse(200, "application/json+protobuf", []byte("[]"), nil)
	}
	var recorded []map[string]any
	c.SetEventHandler(func(evt any) {
		name, isOld, id := goldenEventName(t, evt)
		recorded = append(recorded, map[string]any{"type": name, "isOld": isOld, "id": id})
	})

	// Incoming Message Builders
	encrypt := func(m proto.Message) []byte {
		plain, err := proto.Marshal(m)
		if err != nil {
			t.Fatalf("marshal: %v", err)
		}
		enc, err := c.AuthData.RequestCrypto.Encrypt(plain)
		if err != nil {
			t.Fatalf("encrypt: %v", err)
		}
		return enc
	}
	incoming := func(n int, data *gmproto.RPCMessageData) *gmproto.IncomingRPCMessage {
		data.SessionID = fmt.Sprintf("golden-dispatch-session-%d", n)
		inner, err := proto.Marshal(data)
		if err != nil {
			t.Fatalf("marshal inner: %v", err)
		}
		return &gmproto.IncomingRPCMessage{ResponseID: fmt.Sprintf("golden-dispatch-resp-%d", n), BugleRoute: gmproto.BugleRoute_DataEvent, MessageData: inner}
	}
	updates := func(n int, evt *gmproto.UpdateEvents) *gmproto.IncomingRPCMessage {
		return incoming(n, &gmproto.RPCMessageData{Action: gmproto.ActionType_GET_UPDATES, EncryptedData: encrypt(evt)})
	}
	message := func(n int, id string, ts int64) *gmproto.IncomingRPCMessage {
		return updates(n, &gmproto.UpdateEvents{Event: &gmproto.UpdateEvents_MessageEvent{MessageEvent: &gmproto.MessageEvent{
			Data: []*gmproto.Message{{MessageID: id, ConversationID: "golden-conv", Timestamp: ts}},
		}}})
	}

	multiMessage := func(n int, ids ...string) *gmproto.IncomingRPCMessage {
		var parts []*gmproto.Message
		for _, id := range ids {
			parts = append(parts, &gmproto.Message{MessageID: id, ConversationID: "golden-conv"})
		}
		return updates(n, &gmproto.UpdateEvents{Event: &gmproto.UpdateEvents_MessageEvent{MessageEvent: &gmproto.MessageEvent{Data: parts}}})
	}

	// Steps: Either A Skip Count To Set Or A Message To Handle
	type step struct {
		skipCount *int
		msg       *gmproto.IncomingRPCMessage
	}
	two, one := 2, 1
	steps := []step{
		{skipCount: &two},
		{msg: message(1, "msg-a", 1000)},
		{msg: message(2, "msg-b", 2000)},
		{msg: message(3, "msg-c", 3000)},
		{msg: message(4, "msg-c", 3000)},
		{msg: message(5, "msg-c", 3001)},
		{skipCount: &one},
		{msg: updates(6, &gmproto.UpdateEvents{Event: &gmproto.UpdateEvents_ConversationEvent{ConversationEvent: &gmproto.ConversationEvent{
			Data: []*gmproto.Conversation{{ConversationID: "golden-conv", Name: "Golden"}},
		}}})},
		{msg: updates(7, &gmproto.UpdateEvents{Event: &gmproto.UpdateEvents_TypingEvent{TypingEvent: &gmproto.TypingEvent{
			Data: &gmproto.TypingData{ConversationID: "golden-conv"},
		}}})},
		{msg: incoming(8, &gmproto.RPCMessageData{Action: gmproto.ActionType_GET_UPDATES, UnencryptedData: []byte{0x72, 0x00}})},
		{msg: incoming(9, &gmproto.RPCMessageData{Action: gmproto.ActionType_GET_UPDATES, EncryptedData2: encrypt(&gmproto.EncryptedData2Container{
			AccountChange: &gmproto.AccountChangeOrSomethingEvent{Account: "golden@example.com", Enabled: true},
		})})},
		{msg: updates(10, &gmproto.UpdateEvents{Event: &gmproto.UpdateEvents_UserAlertEvent{UserAlertEvent: &gmproto.UserAlertEvent{
			AlertType: gmproto.AlertType_BROWSER_ACTIVE,
		}}})},
		{msg: updates(11, &gmproto.UpdateEvents{Event: &gmproto.UpdateEvents_ConversationEvent{ConversationEvent: &gmproto.ConversationEvent{
			Data: []*gmproto.Conversation{{ConversationID: "golden-conv-2", Name: "Live"}},
		}}})},
		{msg: multiMessage(12, "msg-d", "msg-e")},
		{msg: multiMessage(13, "msg-d", "msg-e")},
		{msg: multiMessage(14, "msg-f", "msg-f")},
		{msg: updates(15, &gmproto.UpdateEvents{Event: &gmproto.UpdateEvents_SettingsEvent{SettingsEvent: &gmproto.Settings{}}})},
		{msg: updates(16, &gmproto.UpdateEvents{Event: &gmproto.UpdateEvents_AccountChange{AccountChange: &gmproto.AccountChangeOrSomethingEvent{
			Account: "real@example.com", Enabled: true,
		}}})},
		{skipCount: &one},
		{msg: updates(17, &gmproto.UpdateEvents{Event: &gmproto.UpdateEvents_TypingEvent{TypingEvent: &gmproto.TypingEvent{
			Data: &gmproto.TypingData{ConversationID: "golden-conv-old"},
		}}})},
	}

	// Dedup Ring Wrap-Around: 9 Distinct IDs, Then The First Again (Evicted, So Raised) And The Last (Still Held, So Dropped)
	for i := 1; i <= 9; i++ {
		steps = append(steps, step{msg: message(17+i, fmt.Sprintf("ring-%d", i), int64(i))})
	}
	steps = append(steps, step{msg: message(27, "ring-1", 1)}, step{msg: message(28, "ring-9", 9)})

	// Run Them Through Go
	var stepsOut []map[string]any
	for _, s := range steps {
		if s.skipCount != nil {
			c.skipCount = *s.skipCount
			stepsOut = append(stepsOut, map[string]any{"skipCount": *s.skipCount})
			continue
		}
		raw, err := pblite.Marshal(s.msg)
		if err != nil {
			t.Fatalf("pblite marshal: %v", err)
		}
		stepsOut = append(stepsOut, map[string]any{"pblite": string(raw)})
		c.HandleRPCMsg(s.msg)
	}
	finalSkip := c.skipCount
	c.sessionHandler.sendAckRequest()

	// Ack Batches Sent
	var ackBatches [][]string
	for _, req := range ct.Requests {
		if !strings.HasSuffix(req.URL, "/AckMessages") {
			continue
		}
		var ack gmproto.AckMessageRequest
		if err := pblite.Unmarshal([]byte(req.BodyText), &ack); err != nil {
			t.Fatalf("unmarshal ack: %v", err)
		}
		var ids []string
		for _, m := range ack.GetAcks() {
			ids = append(ids, m.GetRequestID())
		}
		ackBatches = append(ackBatches, ids)
	}

	writeGolden(t, out, "dispatch", map[string]any{
		"steps":          stepsOut,
		"events":         recorded,
		"finalSkipCount": finalSkip,
		"ackBatches":     ackBatches,
	})
}

// errTransport fails every request with a network error.
type errTransport struct{}

func (errTransport) RoundTrip(*http.Request) (*http.Response, error) {
	return nil, errors.New("golden network error")
}

// exportRefresh writes refresh.json: the RegisterRefresh request at 30 minutes left, no request at 2 hours left,
// the expiry and TTL Go stores for two response TTLs, and isFatalRefreshError for several failures.
func exportRefresh(t *testing.T, out string) {
	tokenResponse := func(ttl int64) func(*http.Request) *http.Response {
		return func(*http.Request) *http.Response {
			body, err := pblite.Marshal(&gmproto.RegisterRefreshResponse{TokenData: &gmproto.TokenData{TachyonAuthToken: []byte("new-tachyon"), TTL: ttl}})
			if err != nil {
				t.Fatalf("pblite marshal: %v", err)
			}
			return httpResponse(200, "application/json+protobuf", body, nil)
		}
	}

	// Request At 30 Minutes Left
	c, ct := newGoldenClient(t)
	ct.Respond = tokenResponse(3600000000)
	c.AuthData.TachyonExpiry = time.Now().Add(30 * time.Minute)
	if err := c.refreshAuthToken(nil); err != nil {
		t.Fatalf("refreshAuthToken: %v", err)
	}
	request := ct.Requests[0]

	// No Request At 2 Hours Left
	c, ct = newGoldenClient(t)
	c.AuthData.TachyonExpiry = time.Now().Add(2 * time.Hour)
	if err := c.refreshAuthToken(nil); err != nil {
		t.Fatalf("refreshAuthToken 2h: %v", err)
	}
	requestsAt2h := len(ct.Requests)

	// TTL Handling
	ttlCases := map[string]any{}
	for _, ttl := range []int64{0, 3600000000} {
		c, ct = newGoldenClient(t)
		ct.Respond = tokenResponse(ttl)
		var raised []string
		c.SetEventHandler(func(evt any) {
			name, _, _ := goldenEventName(t, evt)
			raised = append(raised, name)
		})
		before := time.Now().UTC()
		if err := c.refreshAuthToken(nil); err != nil {
			t.Fatalf("refreshAuthToken ttl %d: %v", ttl, err)
		}
		ttlCases[fmt.Sprint(ttl)] = map[string]any{
			"expiryOffsetSeconds": c.AuthData.TachyonExpiry.Sub(before).Round(time.Second).Seconds(),
			"tachyonTTL":          c.AuthData.TachyonTTL,
			"tokenHex":            hex.EncodeToString(c.AuthData.TachyonAuthToken),
			"events":              raised,
		}
	}

	// isFatalRefreshError
	invalidCreds, err := pblite.Marshal(events.ErrInvalidCredentials.Data)
	if err != nil {
		t.Fatalf("pblite marshal: %v", err)
	}
	fatalCases := []struct {
		name        string
		status      int
		contentType string
		body        []byte
	}{
		{"401", 401, "", nil},
		{"403", 403, "", nil},
		{"404", 404, "", nil},
		{"500", 500, "", nil},
		{"400InvalidCredentials", 400, "application/json+protobuf", invalidCreds},
		{"network", 0, "", nil},
	}
	var fatal []map[string]any
	for _, fc := range fatalCases {
		c, ct = newGoldenClient(t)
		if fc.status == 0 {
			c.http = &http.Client{Transport: errTransport{}}
		} else {
			ct.Respond = func(*http.Request) *http.Response { return httpResponse(fc.status, fc.contentType, fc.body, nil) }
		}
		err := c.refreshAuthToken(nil)
		if err == nil {
			t.Fatalf("refreshAuthToken %s: expected error", fc.name)
		}
		fatal = append(fatal, map[string]any{
			"name":        fc.name,
			"status":      fc.status,
			"contentType": fc.contentType,
			"bodyHex":     hex.EncodeToString(fc.body),
			"fatal":       isFatalRefreshError(err),
		})
	}

	writeGolden(t, out, "refresh", map[string]any{
		"request":      map[string]any{"url": request.URL, "bodyText": request.BodyText},
		"requestsAt2h": requestsAt2h,
		"ttl":          ttlCases,
		"isFatal":      fatal,
	})
}

// exportLongPollRequest writes longpoll-request.json: the ReceiveMessages request from one doLongPoll
// iteration and the GET_UPDATES request SetActiveSession sends.
func exportLongPollRequest(t *testing.T, out string) {
	// One Long Poll Iteration (Stopped From Inside The Request)
	c, ct := newGoldenClient(t)
	c.AuthData.TachyonExpiry = time.Now().Add(24 * time.Hour)
	ct.Respond = func(*http.Request) *http.Response {
		c.closeLongPolling()
		return httpResponse(200, "application/json+protobuf", []byte("]]"), nil)
	}
	c.doLongPoll(context.Background(), false, false, nil)
	if len(ct.Requests) != 1 {
		t.Fatalf("doLongPoll made %d requests, want 1", len(ct.Requests))
	}
	receive := ct.Requests[0]

	// SetActiveSession
	c, ct = newGoldenClient(t)
	ct.Respond = func(*http.Request) *http.Response {
		return httpResponse(200, "application/json+protobuf", []byte("[]"), nil)
	}
	if err := c.SetActiveSession(context.Background()); err != nil {
		t.Fatalf("SetActiveSession: %v", err)
	}
	active := ct.Requests[0]

	writeGolden(t, out, "longpoll-request", map[string]any{
		"receiveMessages":  map[string]any{"url": receive.URL, "bodyText": receive.BodyText},
		"setActiveSession": map[string]any{"url": active.URL, "bodyText": active.BodyText, "sessionId": c.sessionHandler.sessionID},
	})
}

// exportSignIn writes signin.json: the device ID, the SignInGaia request and the devices and auth data Go derives from
// a canned response, and the session ID and parsed version Go derives from a canned config.
func exportSignIn(t *testing.T, out string) {
	// Canned Response: Mixed-Case Source ID, Three Items (Two Primary), Matching Timestamps
	const (
		uuidA = "aaaaaaaa-1111-2222-3333-444444444444"
		uuidB = "bbbbbbbb-1111-2222-3333-444444444444"
		uuidC = "cccccccc-1111-2222-3333-444444444444"
	)
	type item1 = gmproto.RPCGaiaData_UnknownContainer_Item2_Item1
	type item4 = gmproto.RPCGaiaData_UnknownContainer_Item4
	type item8 = gmproto.RPCGaiaData_UnknownContainer_Item4_Item8
	canned := &gmproto.SignInGaiaResponse{
		Header:           &gmproto.SignInGaiaResponse_Header{UnknownInt2: 2, UnknownTimestamp: 1700000000},
		MaybeBrowserUUID: uuidC,
		DeviceData: &gmproto.SignInGaiaResponse_DeviceData{
			DeviceWrapper: &gmproto.SignInGaiaResponse_DeviceData_DeviceWrapper{
				Device: &gmproto.Device{UserID: 42, SourceID: "Mixed-Case-SRC", Network: "GDitto"},
			},
			UnknownItems2: []*item1{
				{DestOrSourceUUID: uuidA, UnknownInt4: 1, UnknownBigInt7: 777},
				{DestOrSourceUUID: uuidB, UnknownInt4: 6, UnknownBigInt7: 888},
				{DestOrSourceUUID: uuidC, UnknownInt4: 1, UnknownBigInt7: 999},
			},
			UnknownItems3: []*item4{
				{DestOrSourceUUID: uuidA, UnknownTimestampMicroseconds: 1700000001234567, Item8: &item8{UnknownTimestamp: 5551},},
				{DestOrSourceUUID: uuidB, UnknownTimestampMicroseconds: 1700000002000000, Item8: &item8{UnknownTimestamp: 5552}},
				{DestOrSourceUUID: uuidC, UnknownTimestampMicroseconds: 1700000003000000},
			},
		},
		TokenData: &gmproto.TokenData{TachyonAuthToken: []byte("signin-tachyon"), TTL: 7200000000},
	}
	cannedBody, err := pblite.Marshal(canned)
	if err != nil {
		t.Fatalf("pblite marshal: %v", err)
	}

	// Device ID
	c, ct := newGoldenClient(t)
	deviceID := c.baseSignInGaiaPayload().Inner.DeviceID.DeviceID

	// Sign In
	ct.Respond = func(*http.Request) *http.Response {
		return httpResponse(200, "application/json+protobuf", cannedBody, nil)
	}
	before := time.Now().UTC()
	devices, err := c.getGaiaPrimaryDevices(context.Background())
	if err != nil {
		t.Fatalf("getGaiaPrimaryDevices: %v", err)
	}
	request := ct.Requests[0]
	var primary []map[string]any
	for _, d := range devices {
		primary = append(primary, map[string]any{
			"regId":      d.RegID,
			"unknownInt": d.UnknownInt,
			"unknownTs":  d.UnknownTS,
			"lastSeenUs": d.LastSeen.UnixMicro(),
		})
	}
	deviceJSON := func(d *gmproto.Device) string {
		b, mErr := pblite.Marshal(d)
		if mErr != nil {
			t.Fatalf("pblite marshal device: %v", mErr)
		}
		return string(b)
	}

	// Config
	cfg := &gmproto.Config{
		ClientVersion: "20260902",
		DeviceInfo:    &gmproto.Config_DeviceInfo{DeviceID: "11223344-5566-7788-99aa-bbccddeeff00"},
	}
	cfgBody, err := pblite.Marshal(cfg)
	if err != nil {
		t.Fatalf("pblite marshal config: %v", err)
	}
	c2, ct2 := newGoldenClient(t)
	ct2.Respond = func(*http.Request) *http.Response {
		return httpResponse(200, "application/json+protobuf", cfgBody, nil)
	}
	if err = c2.FetchConfig(context.Background()); err != nil {
		t.Fatalf("FetchConfig: %v", err)
	}
	parsed, err := cfg.ParsedClientVersion()
	if err != nil {
		t.Fatalf("ParsedClientVersion: %v", err)
	}

	writeGolden(t, out, "signin", map[string]any{
		"deviceId": deviceID,
		"request":  map[string]any{"url": request.URL, "bodyText": request.BodyText},
		"response": map[string]any{
			"bodyText":            string(cannedBody),
			"primaryDevices":      primary,
			"mobile":              deviceJSON(c.AuthData.Mobile),
			"browser":             deviceJSON(c.AuthData.Browser),
			"tokenHex":            hex.EncodeToString(c.AuthData.TachyonAuthToken),
			"tachyonTTL":          c.AuthData.TachyonTTL,
			"expiryOffsetSeconds": c.AuthData.TachyonExpiry.Sub(before).Round(time.Second).Seconds(),
		},
		"config": map[string]any{
			"bodyText": string(cfgBody),
			"url":      ct2.Requests[0].URL,
			"sessionId": c2.AuthData.SessionID.String(),
			"version":  map[string]any{"year": parsed.Year, "month": parsed.Month, "day": parsed.Day, "v1": parsed.V1, "v2": parsed.V2},
		},
	})
}

// exportPairing writes pairing.json: both emoji lists, the UKEY2 payloads of a fixed-key PairingSession, four
// server init cases (verification version × key derivation version) with Go's emoji, next key and request keys,
// the ProcessServerInit errors, the requests sendGaiaPairingMessage, cancelGaiaPairing and UnpairGaia send, and
// how FinishGaiaPairing maps every GaiaPairingErrorCode.
//
// The request keys come from running the real FinishGaiaPairing (pair_google.go:455-511) against a canned finish
// response delivered through receiveResponse, so the derivation block runs as upstream wrote it instead of a copy.
func exportPairing(t *testing.T, out string) {
	marshal := func(m proto.Message) []byte {
		b, err := proto.Marshal(m)
		if err != nil {
			t.Fatalf("marshal: %v", err)
		}
		return b
	}
	pbliteString := func(m proto.Message) string {
		b, err := pblite.Marshal(m)
		if err != nil {
			t.Fatalf("pblite marshal: %v", err)
		}
		return string(b)
	}

	// Fixed Session (Client Scalar 0x41..0x60)
	sessionUUID := uuid.MustParse("5e551011-2233-4455-6677-8899aabbccdd")
	sessionStart := time.UnixMilli(1700000000123)
	destDevice := primaryDeviceID{RegID: "aaaaaaaa-1111-2222-3333-444444444444", UnknownInt: 777, UnknownTS: 5551}
	newSession := func() *PairingSession {
		priv, err := ecdh.P256().NewPrivateKey(goldenSeq(0x41, 32))
		if err != nil {
			t.Fatalf("client key: %v", err)
		}
		return &PairingSession{UUID: sessionUUID, Start: sessionStart, PairingKeyDSA: priv, DestRegDevice: destDevice}
	}
	ps := newSession()
	initPayload, finishPayload, err := ps.PreparePayloads()
	if err != nil {
		t.Fatalf("PreparePayloads: %v", err)
	}
	var initMsg gmproto.Ukey2Message
	var clientInit gmproto.Ukey2ClientInit
	if err = proto.Unmarshal(initPayload, &initMsg); err != nil {
		t.Fatalf("unmarshal init: %v", err)
	}
	if err = proto.Unmarshal(initMsg.MessageData, &clientInit); err != nil {
		t.Fatalf("unmarshal client init: %v", err)
	}

	// Server Init Builder: Fixed Scalar And Random, X/Y With A Leading 0x00 Like Go's Client
	serverInit := func(scalar, random []byte, verification, derivation int32, tweak func(*gmproto.Ukey2Message, *gmproto.Ukey2ServerInit)) *gmproto.GaiaPairingResponseContainer {
		priv, kErr := ecdh.P256().NewPrivateKey(scalar)
		if kErr != nil {
			t.Fatalf("server key: %v", kErr)
		}
		pub := priv.PublicKey().Bytes()
		si := &gmproto.Ukey2ServerInit{
			Version:         1,
			Random:          random,
			HandshakeCipher: gmproto.Ukey2HandshakeCipher_P256_SHA512,
			PublicKey: &gmproto.GenericPublicKey{
				Type: gmproto.PublicKeyType_EC_P256,
				PublicKey: &gmproto.GenericPublicKey_EcP256PublicKey{EcP256PublicKey: &gmproto.EcP256PublicKey{
					X: append([]byte{0}, pub[1:33]...),
					Y: append([]byte{0}, pub[33:]...),
				}},
			},
		}
		msg := &gmproto.Ukey2Message{MessageType: gmproto.Ukey2Message_SERVER_INIT}
		if tweak != nil {
			tweak(msg, si)
		}
		msg.MessageData = marshal(si)
		return &gmproto.GaiaPairingResponseContainer{
			UnknownInt3:                      1,
			SessionUUID:                      sessionUUID.String(),
			Data:                             marshal(msg),
			ConfirmedVerificationCodeVersion: verification,
			ConfirmedKeyDerivationVersion:    derivation,
		}
	}

	// Client Whose Send Requests Get A Canned Response Through receiveResponse
	respondingClient := func(response *gmproto.GaiaPairingResponseContainer) (*Client, *captureTransport) {
		c, ct := newGoldenClient(t)
		ct.Respond = func(*http.Request) *http.Response {
			last := ct.Requests[len(ct.Requests)-1]
			body, _ := hex.DecodeString(last.BodyHex)
			var outgoing gmproto.OutgoingRPCMessage
			if uErr := pblite.Unmarshal(body, &outgoing); uErr == nil && response != nil {
				var data gmproto.OutgoingRPCData
				if uErr = proto.Unmarshal(outgoing.GetData().GetMessageData(), &data); uErr == nil {
					c.sessionHandler.receiveResponse(&IncomingRPCMessage{
						IncomingRPCMessage: &gmproto.IncomingRPCMessage{ResponseID: "golden-resp"},
						Message: &gmproto.RPCMessageData{
							SessionID:       outgoing.GetData().GetRequestID(),
							Action:          data.Action,
							UnencryptedData: marshal(response),
						},
					})
				}
			}
			return httpResponse(200, "application/json+protobuf", []byte("[]"), nil)
		}
		return c, ct
	}

	// Server Init Cases: Emoji, Next Key And Request Keys
	var cases []map[string]any
	for i, v := range []struct{ verification, derivation int32 }{{0, 0}, {0, 1}, {1, 0}, {1, 1}} {
		scalar := goldenSeq(0x10+i*0x20, 32)
		random := goldenSeq(0x80+i*0x10, 32)
		container := serverInit(scalar, random, v.verification, v.derivation, nil)
		cps := newSession()
		cps.InitPayload = initPayload
		emoji, pErr := cps.ProcessServerInit(container)
		if pErr != nil {
			t.Fatalf("ProcessServerInit case %d: %v", i, pErr)
		}
		c, _ := respondingClient(&gmproto.GaiaPairingResponseContainer{})
		phoneID, fErr := c.FinishGaiaPairing(context.Background(), cps)
		if fErr != nil {
			t.Fatalf("FinishGaiaPairing case %d: %v", i, fErr)
		}
		cases = append(cases, map[string]any{
			"verificationVersion":  v.verification,
			"keyDerivationVersion": v.derivation,
			"serverScalarHex":      hex.EncodeToString(scalar),
			"serverRandomHex":      hex.EncodeToString(random),
			"containerHex":         hex.EncodeToString(marshal(container)),
			"containerPblite":      pbliteString(container),
			"emoji":                emoji,
			"nextKeyHex":           hex.EncodeToString(cps.NextKey),
			"aesKeyHex":            hex.EncodeToString(c.AuthData.RequestCrypto.AESKey),
			"hmacKeyHex":           hex.EncodeToString(c.AuthData.RequestCrypto.HMACKey),
			"pairingId":            c.AuthData.PairingID.String(),
			"destPhoneId":          c.AuthData.DestPhoneID,
			"phoneId":              phoneID,
		})
	}

	// ProcessServerInit Errors
	validScalar, validRandom := goldenSeq(0x10, 32), goldenSeq(0x80, 32)
	errorCases := map[string]*gmproto.GaiaPairingResponseContainer{
		"wrongMessageType": serverInit(validScalar, validRandom, 0, 0, func(m *gmproto.Ukey2Message, _ *gmproto.Ukey2ServerInit) {
			m.MessageType = gmproto.Ukey2Message_CLIENT_INIT
		}),
		"version2": serverInit(validScalar, validRandom, 0, 0, func(_ *gmproto.Ukey2Message, si *gmproto.Ukey2ServerInit) { si.Version = 2 }),
		"wrongCipher": serverInit(validScalar, validRandom, 0, 0, func(_ *gmproto.Ukey2Message, si *gmproto.Ukey2ServerInit) {
			si.HandshakeCipher = gmproto.Ukey2HandshakeCipher_CURVE25519_SHA512
		}),
		"random31": serverInit(validScalar, goldenSeq(0x80, 31), 0, 0, nil),
		"xPrefix": serverInit(validScalar, validRandom, 0, 0, func(_ *gmproto.Ukey2Message, si *gmproto.Ukey2ServerInit) {
			si.PublicKey.GetEcP256PublicKey().X[0] = 1
		}),
		"coordinateLengths": serverInit(validScalar, validRandom, 0, 0, func(_ *gmproto.Ukey2Message, si *gmproto.Ukey2ServerInit) {
			key := si.PublicKey.GetEcP256PublicKey()
			key.X = key.X[2:]
		}),
		"verificationVersion2": serverInit(validScalar, validRandom, 2, 0, nil),
	}
	serverInitErrors := map[string]any{}
	for name, container := range errorCases {
		eps := newSession()
		eps.InitPayload = initPayload
		_, pErr := eps.ProcessServerInit(container)
		if pErr == nil {
			t.Fatalf("ProcessServerInit %s: expected an error", name)
		}
		serverInitErrors[name] = map[string]any{"containerHex": hex.EncodeToString(marshal(container)), "error": pErr.Error()}
	}

	// Requests: Client Init, Client Finished, Cancel, Unpair
	requests := map[string]any{}
	c, ct := respondingClient(errorCases["version2"])
	if _, err = c.sendGaiaPairingMessage(context.Background(), ps, gmproto.ActionType_CREATE_GAIA_PAIRING_CLIENT_INIT, initPayload); err != nil {
		t.Fatalf("sendGaiaPairingMessage init: %v", err)
	}
	if _, err = c.sendGaiaPairingMessage(context.Background(), ps, gmproto.ActionType_CREATE_GAIA_PAIRING_CLIENT_FINISHED, finishPayload); err != nil {
		t.Fatalf("sendGaiaPairingMessage finish: %v", err)
	}
	if err = c.cancelGaiaPairing(context.Background(), ps); err != nil {
		t.Fatalf("cancelGaiaPairing: %v", err)
	}
	c.AuthData.PairingID = uuid.MustParse("01020304-0506-0708-090a-0b0c0d0e0f10")
	if err = c.UnpairGaia(context.Background()); err != nil {
		t.Fatalf("UnpairGaia: %v", err)
	}
	for i, name := range []string{"clientInit", "clientFinished", "cancel", "unpair"} {
		requests[name] = map[string]any{"url": ct.Requests[i].URL, "bodyText": ct.Requests[i].BodyText}
	}

	// Finish Error Codes: Every Code With FinishErrorType 1
	var finishErrors []map[string]any
	for code := int32(0); code < int32(len(gmproto.GaiaPairingErrorCode_name)); code++ {
		fps := newSession()
		fps.InitPayload = initPayload
		fc, _ := respondingClient(&gmproto.GaiaPairingResponseContainer{FinishErrorType: 1, FinishErrorCode: gmproto.GaiaPairingErrorCode(code)})
		_, fErr := fc.FinishGaiaPairing(context.Background(), fps)
		if fErr == nil {
			t.Fatalf("FinishGaiaPairing code %d: expected an error", code)
		}
		name := "Unknown"
		switch {
		case errors.Is(fErr, ErrIncorrectEmoji):
			name = "IncorrectEmoji"
		case errors.Is(fErr, ErrPairingCancelled) && strings.Contains(fErr.Error(), "not me"):
			name = "NotMe"
		case errors.Is(fErr, ErrPairingCancelled):
			name = "Canceled"
		case errors.Is(fErr, ErrPairingTimeout):
			name = "Timeout"
		}
		finishErrors = append(finishErrors, map[string]any{
			"code":     code,
			"codeName": gmproto.GaiaPairingErrorCode(code).String(),
			"failure":  name,
			"error":    fErr.Error(),
		})
	}

	writeGolden(t, out, "pairing", map[string]any{
		"emojisV0": pairingEmojisV0,
		"emojisV1": pairingEmojisV1,
		"session": map[string]any{
			"uuid":             sessionUUID.String(),
			"startUnixMilli":   sessionStart.UnixMilli(),
			"clientScalarHex":  hex.EncodeToString(goldenSeq(0x41, 32)),
			"initPayloadHex":   hex.EncodeToString(initPayload),
			"initRandomHex":    hex.EncodeToString(clientInit.Random),
			"finishPayloadHex": hex.EncodeToString(finishPayload),
			"destRegId":        destDevice.RegID,
			"destUnknownInt":   destDevice.UnknownInt,
			"destUnknownTs":    destDevice.UnknownTS,
		},
		"serverInit":       cases,
		"serverInitErrors": serverInitErrors,
		"requests":         requests,
		"finishErrors":     finishErrors,
	})
}

// gatedReader returns head, blocks until gate closes, then returns tail. It stands in for a long poll that stays open.
type gatedReader struct {
	head, tail []byte
	gate       <-chan struct{}
	opened     bool
}

func (gr *gatedReader) Read(p []byte) (int, error) {
	if len(gr.head) > 0 {
		n := copy(p, gr.head)
		gr.head = gr.head[n:]
		return n, nil
	}
	if !gr.opened {
		<-gr.gate
		gr.opened = true
	}
	if len(gr.tail) > 0 {
		n := copy(p, gr.tail)
		gr.tail = gr.tail[n:]
		return n, nil
	}
	return 0, io.EOF
}

func (gr *gatedReader) Close() error { return nil }

// exportConnect writes connect.json: every request Connect() sends on a logged-in client whose token has 30 minutes
// left, in order, up to postConnect's GET_UPDATES (10 s guard). The canned responses are written too, so the C# test
// can serve the same ones. Random values (refresh request ID, timestamp and signature, listen ID, session ID) are
// left as Go produced them; the C# test copies them over before comparing.
func exportConnect(t *testing.T, out string) {
	c, ct := newGoldenClient(t)
	c.AuthData.TachyonExpiry = time.Now().Add(30 * time.Minute)

	// Canned Responses
	refreshBody, err := pblite.Marshal(&gmproto.RegisterRefreshResponse{TokenData: &gmproto.TokenData{TachyonAuthToken: []byte("new-tachyon"), TTL: 86400000000}})
	if err != nil {
		t.Fatalf("pblite marshal refresh: %v", err)
	}
	ackPayload, err := pblite.Marshal(&gmproto.LongPollingPayload{Ack: &gmproto.StartAckMessage{Count: proto.Int32(0)}})
	if err != nil {
		t.Fatalf("pblite marshal ack: %v", err)
	}
	longPollHead := append([]byte("[["), ackPayload...)
	emptyBody := []byte("[]")

	// Answer By Path; The Long Poll Stays Open Until Capture Ends
	gate := make(chan struct{})
	sentUpdates := make(chan int, 1)
	ct.Respond = func(req *http.Request) *http.Response {
		switch path := req.URL.Path; {
		case strings.HasSuffix(path, "/RegisterRefresh"):
			return httpResponse(200, "application/json+protobuf", refreshBody, nil)
		case strings.HasSuffix(path, "/ReceiveMessages"):
			return &http.Response{
				StatusCode: 200,
				Header:     http.Header{"Content-Type": {"application/json+protobuf"}},
				Body:       &gatedReader{head: longPollHead, tail: []byte("]]"), gate: gate},
			}
		case strings.HasSuffix(path, "/SendMessage"):
			// The First SendMessage Is GET_UPDATES; Capture Ends With It (Go Sends IS_BUGLE_DEFAULT Next)
			select {
			case sentUpdates <- len(ct.Requests):
			default:
			}
			return httpResponse(200, "application/json+protobuf", emptyBody, nil)
		default:
			return httpResponse(200, "application/json+protobuf", emptyBody, nil)
		}
	}

	// Connect And Wait For GET_UPDATES
	if err = c.Connect(context.Background()); err != nil {
		t.Fatalf("Connect: %v", err)
	}
	var captureEnd int
	select {
	case captureEnd = <-sentUpdates:
	case <-time.After(10 * time.Second):
		t.Fatalf("Connect did not send GET_UPDATES within 10 s")
	}
	captured := append([]capturedRequest(nil), ct.Requests[:captureEnd]...)
	sessionID := c.sessionHandler.sessionID
	c.Disconnect()
	close(gate)

	var requests []map[string]any
	for _, r := range captured {
		requests = append(requests, map[string]any{"method": r.Method, "url": r.URL, "bodyText": r.BodyText})
	}
	writeGolden(t, out, "connect", map[string]any{
		"responses": map[string]any{
			"refresh":  string(refreshBody),
			"longPoll": string(longPollHead),
			"ack":      string(emptyBody),
			"send":     string(emptyBody),
		},
		"sessionId": sessionID,
		"requests":  requests,
	})
}
