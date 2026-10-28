package libgm

// Golden vector exporter. Copied into pkg/libgm of the pinned mautrix-gmessages
// checkout by Export-Golden.ps1 and run as TestExportGolden. Each export<Area>
// writes GOLDEN_OUT/<area>.json for the C# tests to match.

import (
	"bytes"
	"crypto/ecdsa"
	"crypto/elliptic"
	"encoding/hex"
	"encoding/json"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"testing"

	"github.com/google/uuid"
	"github.com/rs/zerolog"
	"google.golang.org/protobuf/proto"
	"go.mau.fi/util/exhttp"
	"go.mau.fi/util/pblite"

	"go.mau.fi/mautrix-gmessages/pkg/libgm/crypto"
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

	// Flatten Headers (first value only; Cookie is joined by net/http already)
	headers := map[string]string{}
	for name, values := range req.Header {
		headers[name] = values[0]
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
