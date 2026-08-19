package registry

import (
	"context"
	"fmt"
	"net/http"
	"net/http/httptest"
	"testing"
)

func TestScannerWorkSessionsAcceptsCenterCollectionNames(t *testing.T) {
	tests := []struct{ name, field string }{
		{name: "items", field: "items"},
		{name: "sessions", field: "sessions"},
		{name: "work sessions", field: "work_sessions"},
		{name: "tasks", field: "tasks"},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			center := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				w.Header().Set("Content-Type", "application/json")
				_, _ = fmt.Fprintf(w, `{"data":{"%s":[{"id":99,"binding_id":7,"user_id":42,"operation_type_code":"PACKING","operation_type_name":"打包","expires_at":"2026-08-14T08:30:00Z"}]}}`, tt.field)
			}))
			defer center.Close()

			client := New(Config{CenterURL: center.URL, APIToken: "edge-token", NodeID: "edge-1", NamespaceID: 2})
			sessions, err := client.ScannerWorkSessions(context.Background())
			if err != nil {
				t.Fatal(err)
			}
			if len(sessions) != 1 || sessions[0].ID != 99 || sessions[0].BindingID != 7 || sessions[0].OperationTypeCode != "PACKING" {
				t.Fatalf("unexpected work sessions: %+v", sessions)
			}
		})
	}
}
