package proxy

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/http/httputil"
	"net/url"
	"sort"
	"strings"
	"sync"
	"time"

	"fscm-edge/internal/cache"
)

var (
	errUpstreamUnavailable = errors.New("center unavailable")
	errResponseTooLarge    = errors.New("response too large for cache path")
)

type Config struct {
	CenterURL      string
	NodeID         string
	CacheWhitelist []string
	StaleIfError   bool
	MaxObjectBytes int64
	ConnectTimeout time.Duration
	HeaderTimeout  time.Duration
}

type CenterStatus struct {
	Reachable   bool      `json:"reachable"`
	LastOKAt    time.Time `json:"last_ok_at,omitempty"`
	LastError   string    `json:"last_error,omitempty"`
	LastErrorAt time.Time `json:"last_error_at,omitempty"`
}

type Handler struct {
	cfg       Config
	target    *url.URL
	client    *http.Client
	reverse   *httputil.ReverseProxy
	cache     *cache.Cache
	coalescer *cache.Coalescer
	statusMu  sync.RWMutex
	status    CenterStatus
}

func New(cfg Config, responseCache *cache.Cache) (*Handler, error) {
	target, err := url.Parse(strings.TrimRight(strings.TrimSpace(cfg.CenterURL), "/"))
	if err != nil || target.Scheme == "" || target.Host == "" || (target.Scheme != "http" && target.Scheme != "https") {
		return nil, fmt.Errorf("invalid center_url")
	}
	if cfg.ConnectTimeout <= 0 {
		cfg.ConnectTimeout = 3 * time.Second
	}
	if cfg.HeaderTimeout <= 0 {
		cfg.HeaderTimeout = 30 * time.Second
	}
	transport := &http.Transport{
		Proxy:                 http.ProxyFromEnvironment,
		DialContext:           (&net.Dialer{Timeout: cfg.ConnectTimeout, KeepAlive: 30 * time.Second}).DialContext,
		ResponseHeaderTimeout: cfg.HeaderTimeout,
	}
	h := &Handler{cfg: cfg, target: target, client: &http.Client{Transport: transport}, cache: responseCache, coalescer: cache.NewCoalescer()}
	h.reverse = httputil.NewSingleHostReverseProxy(target)
	h.reverse.Transport = transport
	original := h.reverse.Director
	h.reverse.Director = func(request *http.Request) { original(request); h.prepareRequest(request) }
	h.reverse.ModifyResponse = func(response *http.Response) error {
		h.centerOK()
		response.Header.Set("X-FSCM-Cache", "BYPASS")
		response.Header.Set("X-FSCM-Served-By", "center-via-edge")
		if isMutation(response.Request.Method) && response.StatusCode >= 200 && response.StatusCode < 300 {
			h.cache.InvalidateNamespace(response.Request.Header.Get("X-Namespace-ID"))
		}
		return nil
	}
	h.reverse.ErrorHandler = func(writer http.ResponseWriter, _ *http.Request, proxyErr error) {
		h.centerError(proxyErr)
		writeUnavailable(writer)
	}
	return h, nil
}

func (h *Handler) ServeHTTP(writer http.ResponseWriter, request *http.Request) {
	if !h.cacheEligible(request) {
		h.cache.RecordBypass()
		h.reverse.ServeHTTP(writer, request)
		return
	}

	now := time.Now()
	key := cacheKey(request)
	h.cache.TrackPath(request.URL.Path)
	lookup := h.cache.Get(key, now)
	if lookup.Found && lookup.Fresh {
		writeSnapshot(writer, lookup.Response, "HIT", false)
		return
	}

	result, _ := h.coalescer.Do(key, func() cache.Result {
		response, err := h.fetchCacheCandidate(request)
		if err == nil && h.safeToStore(response) {
			h.cache.Put(key, request.Header.Get("X-Namespace-ID"), request.URL.Path, response, time.Now())
		}
		return cache.Result{Response: response, Err: err}
	})
	if result.Err != nil {
		if errors.Is(result.Err, errResponseTooLarge) {
			h.cache.RecordBypass()
			h.reverse.ServeHTTP(writer, request)
			return
		}
		if h.cfg.StaleIfError && lookup.Found {
			h.cache.RecordStaleHit()
			writeSnapshot(writer, lookup.Response, "STALE", true)
			return
		}
		writeUnavailable(writer)
		return
	}
	writeSnapshot(writer, result.Response, "MISS", false)
}

func (h *Handler) fetchCacheCandidate(original *http.Request) (cache.Response, error) {
	request := original.Clone(original.Context())
	request.URL.Scheme, request.URL.Host = h.target.Scheme, h.target.Host
	request.Host, request.RequestURI = h.target.Host, ""
	h.prepareRequest(request)
	response, err := h.client.Do(request)
	if err != nil {
		h.centerError(err)
		return cache.Response{}, errUpstreamUnavailable
	}
	defer response.Body.Close()
	if response.ContentLength > h.cfg.MaxObjectBytes && response.ContentLength >= 0 {
		return cache.Response{}, errResponseTooLarge
	}
	body, err := io.ReadAll(io.LimitReader(response.Body, h.cfg.MaxObjectBytes+1))
	if err != nil {
		return cache.Response{}, err
	}
	if int64(len(body)) > h.cfg.MaxObjectBytes {
		return cache.Response{}, errResponseTooLarge
	}
	h.centerOK()
	snapshot := cache.Response{StatusCode: response.StatusCode, Header: safeHeaders(response.Header), Body: body}
	if response.StatusCode == http.StatusBadGateway || response.StatusCode == http.StatusServiceUnavailable || response.StatusCode == http.StatusGatewayTimeout {
		h.centerError(fmt.Errorf("center returned %d", response.StatusCode))
		return snapshot, errUpstreamUnavailable
	}
	return snapshot, nil
}

func (h *Handler) cacheEligible(request *http.Request) bool {
	if !h.cache.Enabled() {
		return false
	}
	if request.Method != http.MethodGet && request.Method != http.MethodHead {
		return false
	}
	path := strings.ToLower(request.URL.Path)
	for _, blocked := range []string{"/auth", "/users", "/permissions", "/menus", "/config", "/admin", "export", "download", "upload", "template"} {
		if strings.Contains(path, blocked) {
			return false
		}
	}
	for _, prefix := range h.cfg.CacheWhitelist {
		prefix = strings.TrimRight(strings.ToLower(strings.TrimSpace(prefix)), "/")
		if path == prefix {
			return true
		}
		if strings.HasPrefix(path, prefix+"/") && !strings.Contains(strings.TrimPrefix(path, prefix+"/"), "/") {
			return true
		}
	}
	return false
}

func (h *Handler) safeToStore(response cache.Response) bool {
	return response.StatusCode == http.StatusOK && strings.HasPrefix(strings.ToLower(response.Header.Get("Content-Type")), "application/json") && !cache.CacheControlDisallows(response.Header)
}

func (h *Handler) prepareRequest(request *http.Request) {
	removeHopHeaders(request.Header)
	request.Header.Del("X-Edge-Ticket")
	request.Header.Del("X-Edge-Admin-Token")
	request.Header.Del("X-Edge-Node-Token")
	request.Header.Set("X-FSCM-Edge-Node-ID", h.cfg.NodeID)
}

func (h *Handler) CenterStatus() CenterStatus {
	h.statusMu.RLock()
	defer h.statusMu.RUnlock()
	return h.status
}

func (h *Handler) Probe(ctx context.Context) error {
	request, _ := http.NewRequestWithContext(ctx, http.MethodGet, h.target.String()+"/health", nil)
	response, err := h.client.Do(request)
	if err != nil {
		h.centerError(err)
		return err
	}
	defer response.Body.Close()
	if response.StatusCode < 200 || response.StatusCode >= 500 {
		err = fmt.Errorf("center health returned %d", response.StatusCode)
		h.centerError(err)
		return err
	}
	h.centerOK()
	return nil
}

func (h *Handler) centerOK() {
	h.statusMu.Lock()
	h.status.Reachable = true
	h.status.LastOKAt = time.Now()
	h.status.LastError = ""
	h.statusMu.Unlock()
}

func (h *Handler) centerError(err error) {
	h.statusMu.Lock()
	h.status.Reachable = false
	h.status.LastError = err.Error()
	h.status.LastErrorAt = time.Now()
	h.statusMu.Unlock()
}

func cacheKey(request *http.Request) string {
	query := request.URL.Query()
	for key := range query {
		sort.Strings(query[key])
	}
	identity := request.Header.Get("Authorization") + "\x00" + request.Header.Get("X-API-Token")
	identityHash := sha256.Sum256([]byte(identity))
	raw := strings.Join([]string{request.Method, request.URL.Path, query.Encode(), request.Header.Get("X-Namespace-ID"), hex.EncodeToString(identityHash[:]), request.Header.Get("Accept"), request.Header.Get("Accept-Language"), request.Header.Get("Accept-Encoding")}, "\n")
	sum := sha256.Sum256([]byte(raw))
	return hex.EncodeToString(sum[:])
}

func writeSnapshot(writer http.ResponseWriter, response cache.Response, state string, stale bool) {
	copyHeaders(writer.Header(), response.Header)
	if !response.StoredAt.IsZero() {
		age := int(time.Since(response.StoredAt).Seconds())
		if age < 0 {
			age = 0
		}
		writer.Header().Set("Age", fmt.Sprintf("%d", age))
	}
	writer.Header().Set("X-FSCM-Cache", state)
	writer.Header().Set("X-FSCM-Served-By", "edge-cache")
	if stale {
		writer.Header().Set("Warning", `110 - "Response is stale"`)
	}
	writer.WriteHeader(response.StatusCode)
	if len(response.Body) > 0 {
		_, _ = writer.Write(response.Body)
	}
}

func writeUnavailable(writer http.ResponseWriter) {
	writer.Header().Set("Content-Type", "application/json")
	writer.WriteHeader(http.StatusBadGateway)
	_ = json.NewEncoder(writer).Encode(map[string]string{"code": "EDGE_CENTER_UNAVAILABLE", "message": "中心服务当前不可用，且本地没有可用缓存"})
}

func safeHeaders(source http.Header) http.Header {
	result := make(http.Header)
	for key, values := range source {
		if isHopHeader(key) {
			continue
		}
		result[key] = append([]string(nil), values...)
	}
	return result
}

func copyHeaders(destination, source http.Header) {
	for key, values := range source {
		destination[key] = append([]string(nil), values...)
	}
}
func removeHopHeaders(header http.Header) {
	for _, name := range strings.Split(header.Get("Connection"), ",") {
		if name = strings.TrimSpace(name); name != "" {
			header.Del(name)
		}
	}
	for key := range header {
		if isHopHeader(key) {
			header.Del(key)
		}
	}
}
func isHopHeader(key string) bool {
	switch strings.ToLower(key) {
	case "connection", "proxy-connection", "keep-alive", "proxy-authenticate", "proxy-authorization", "te", "trailer", "transfer-encoding", "upgrade":
		return true
	}
	return false
}
func isMutation(method string) bool {
	return method == http.MethodPost || method == http.MethodPut || method == http.MethodPatch || method == http.MethodDelete
}
