package registry

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"testing"
)

func TestScannerWorkSessionCommandsUseCenterEndpoints(t *testing.T) {
	var paths []string
	var payloads []map[string]interface{}
	center := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		paths = append(paths, r.URL.Path)
		var payload map[string]interface{}
		if err := json.NewDecoder(r.Body).Decode(&payload); err != nil {
			t.Fatal(err)
		}
		payloads = append(payloads, payload)
		w.Header().Set("Content-Type", "application/json")
		if r.URL.Path == "/api/edge/scanners/work-sessions/start" {
			_, _ = w.Write([]byte(`{"code":0,"data":{"id":99,"binding_id":7,"user_id":42,"operation_type_code":"PACKING"}}`))
			return
		}
		_, _ = w.Write([]byte(`{"code":0,"data":null}`))
	}))
	defer center.Close()

	client := New(Config{CenterURL: center.URL, APIToken: "edge-token", NodeID: "edge-1", NamespaceID: 2})
	session, err := client.StartScannerWorkSession(context.Background(), map[string]interface{}{
		"binding_id": uint(7), "operation_type_code": "PACKING",
	})
	if err != nil {
		t.Fatal(err)
	}
	if session.ID != 99 || session.OperationTypeCode != "PACKING" {
		t.Fatalf("unexpected start response: %+v", session)
	}
	if err := client.EndScannerWorkSession(context.Background(), map[string]interface{}{
		"binding_id": uint(7), "end_reason": "operator_scan",
	}); err != nil {
		t.Fatal(err)
	}

	if len(paths) != 2 ||
		paths[0] != "/api/edge/scanners/work-sessions/start" ||
		paths[1] != "/api/edge/scanners/work-sessions/end" {
		t.Fatalf("unexpected paths: %+v", paths)
	}
	if payloads[0]["operation_type_code"] != "PACKING" || payloads[1]["end_reason"] != "operator_scan" {
		t.Fatalf("unexpected payloads: %+v", payloads)
	}
}
