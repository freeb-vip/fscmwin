package main

import (
	"context"
	"errors"
	"net/http"
	"net/http/httptest"
	"path/filepath"
	"strings"
	"testing"

	"fscm-edge/internal/storage"

	"github.com/gin-gonic/gin"
)

func TestStorageRoutesRequireAdminTokenAndValidateRanges(t *testing.T) {
	gin.SetMode(gin.TestMode)
	store, err := storage.Open(filepath.Join(t.TempDir(), "edge.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer store.Close()
	manager, err := storage.NewManager(store, "test-node", routeProvisioner{})
	if err != nil {
		t.Fatal(err)
	}
	router := gin.New()
	admin := router.Group("/edge", adminMiddleware("secret"))
	registerStorageRoutes(admin, storageRuntime{manager: manager})

	unauthorized := httptest.NewRecorder()
	router.ServeHTTP(unauthorized, httptest.NewRequest(http.MethodGet, "/edge/storage/config", nil))
	if unauthorized.Code != http.StatusUnauthorized {
		t.Fatalf("unauthorized status=%d", unauthorized.Code)
	}

	request := httptest.NewRequest(http.MethodPut, "/edge/storage/config", strings.NewReader(`{"enabled":true,"local_path":"C:\\recordings","retention_days":0,"reserve_free_gb":10}`))
	request.Header.Set("Content-Type", "application/json")
	request.Header.Set("X-Edge-Admin-Token", "secret")
	response := httptest.NewRecorder()
	router.ServeHTTP(response, request)
	if response.Code != http.StatusBadRequest || !strings.Contains(response.Body.String(), "INVALID_STORAGE_CONFIG") {
		t.Fatalf("invalid config status=%d body=%s", response.Code, response.Body.String())
	}

	request = httptest.NewRequest(http.MethodPut, "/edge/storage/config", strings.NewReader(`{"enabled":false,"retention_days":7,"reserve_free_gb":10,"smb_compatibility_mode":"legacy_insecure"}`))
	request.Header.Set("Content-Type", "application/json")
	request.Header.Set("X-Edge-Admin-Token", "secret")
	response = httptest.NewRecorder()
	router.ServeHTTP(response, request)
	if response.Code != http.StatusBadRequest || !strings.Contains(response.Body.String(), "INVALID_STORAGE_CONFIG") {
		t.Fatalf("invalid compatibility mode status=%d body=%s", response.Code, response.Body.String())
	}
}

func TestStorageRoutesRemainDiagnosableWhenNASIsDegraded(t *testing.T) {
	gin.SetMode(gin.TestMode)
	router := gin.New()
	admin := router.Group("/edge", adminMiddleware("secret"))
	registerStorageRoutes(admin, unavailableStorageRuntime(errors.New("storage migration failed")))

	statusRequest := httptest.NewRequest(http.MethodGet, "/edge/storage/status", nil)
	statusRequest.Header.Set("X-Edge-Admin-Token", "secret")
	statusResponse := httptest.NewRecorder()
	router.ServeHTTP(statusResponse, statusRequest)
	if statusResponse.Code != http.StatusOK || !strings.Contains(statusResponse.Body.String(), `"state":"degraded"`) {
		t.Fatalf("degraded status=%d body=%s", statusResponse.Code, statusResponse.Body.String())
	}

	configRequest := httptest.NewRequest(http.MethodGet, "/edge/storage/config", nil)
	configRequest.Header.Set("X-Edge-Admin-Token", "secret")
	configResponse := httptest.NewRecorder()
	router.ServeHTTP(configResponse, configRequest)
	if configResponse.Code != http.StatusServiceUnavailable || !strings.Contains(configResponse.Body.String(), "STORAGE_UNAVAILABLE") {
		t.Fatalf("unavailable config status=%d body=%s", configResponse.Code, configResponse.Body.String())
	}
}

type routeProvisioner struct{}

func (routeProvisioner) Configure(context.Context, storage.Config, string) (string, error) {
	return "S-1-test", nil
}
func (routeProvisioner) Disable(context.Context, storage.Config) error { return nil }
func (routeProvisioner) Inspect(context.Context, storage.Config) storage.ProvisionStatus {
	return storage.ProvisionStatus{}
}
func (routeProvisioner) Remove(context.Context, storage.Config) error { return nil }
