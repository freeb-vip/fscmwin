package main

import (
	"context"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/hex"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"math"
	"net"
	"net/http"
	"os"
	"os/signal"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"sync"
	"syscall"
	"time"
	"unicode/utf8"

	"fscm-edge/internal/cache"
	"fscm-edge/internal/catalog"
	"fscm-edge/internal/config"
	catalogmedia "fscm-edge/internal/media"
	"fscm-edge/internal/printing"
	edgeproxy "fscm-edge/internal/proxy"
	"fscm-edge/internal/registry"
	"fscm-edge/internal/version"

	"github.com/gin-gonic/gin"
	"github.com/grandcat/zeroconf"
	"golang.org/x/net/websocket"
)

type printerAvailability struct {
	sync.RWMutex
	printers map[string]struct{}
	ready    bool
}

func (a *printerAvailability) Set(printers []string) {
	updated := make(map[string]struct{}, len(printers))
	for _, printer := range printers {
		if name := strings.TrimSpace(printer); name != "" {
			updated[name] = struct{}{}
		}
	}
	a.Lock()
	a.printers = updated
	a.ready = true
	a.Unlock()
}

func (a *printerAvailability) Has(printer string) bool {
	a.RLock()
	defer a.RUnlock()
	_, ok := a.printers[strings.TrimSpace(printer)]
	return ok
}

func (a *printerAvailability) Ready() bool {
	a.RLock()
	defer a.RUnlock()
	return a.ready
}

func main() {
	configPath := flag.String("config", "edge.config.yaml", "edge config yaml")
	_ = flag.String("mode", "edge", "compatibility flag")
	flag.Parse()
	cfg, err := config.Load(*configPath)
	if err != nil {
		panic(err)
	}
	hostname, _ := os.Hostname()
	hostname = strings.TrimSpace(hostname)
	if strings.TrimSpace(cfg.NodeID) == "" {
		// Node identity must not depend on a display name, which may be shared by many installations.
		cfg.NodeID = first(hostname, "fscm-edge-node")
	}
	if strings.TrimSpace(cfg.NodeName) == "" {
		cfg.NodeName = first(hostname, cfg.NodeID)
	}
	if cfg.LANBaseURL == "" {
		if address := localIPv4(); address != "" {
			cfg.LANBaseURL = "http://" + address + ":" + cfg.Port
		}
	}

	responseCache := cache.New(cache.Config{Mode: cache.Mode(cfg.Cache.Mode), MaxEntries: cfg.Cache.MaxEntries, MaxBytes: cfg.Cache.MaxMemoryMB << 20, MaxObjectBytes: cfg.Cache.MaxObjectMB << 20, MaxStale: time.Duration(cfg.Cache.MaxStaleHours) * time.Hour})
	proxyHandler, err := edgeproxy.New(edgeproxy.Config{CenterURL: cfg.CenterURL, NodeID: cfg.NodeID, CacheWhitelist: cfg.Cache.Whitelist, StaleIfError: cfg.Cache.StaleIfError, MaxObjectBytes: cfg.Cache.MaxObjectMB << 20}, responseCache)
	if err != nil {
		panic(err)
	}
	templatesPath := filepath.Join(filepath.Dir(*configPath), "print-templates.json")
	printer, err := printing.New(printing.Config{DefaultPrinter: cfg.DefaultPrinter, Template: cfg.PrintTemplate, WidthMM: cfg.PrintWidthMM, HeightMM: cfg.PrintHeightMM, Orientation: cfg.PrintOrientation, Mode: cfg.PrintMode, Copies: cfg.PrintCopies, TemplatesPath: templatesPath, JobsPath: cfg.JobsPath, QRCodePrefix: cfg.SkuQRPrefix})
	if err != nil {
		panic(err)
	}
	defer func() { _ = printer.Close() }()
	catalogStore, err := catalog.Open(cfg.JobsPath)
	if err != nil {
		panic(err)
	}
	defer func() { _ = catalogStore.Close() }()
	catalogManager := catalog.NewManager(catalog.Config{CenterURL: cfg.CenterURL, APIToken: cfg.APIToken, NodeID: cfg.NodeID, NamespaceID: cfg.NamespaceID, SKUQRPrefix: cfg.SkuQRPrefix}, catalogStore)
	mediaCache, err := catalogmedia.Open(catalogmedia.Config{
		Path: cfg.MediaCache.Path, MaxBytes: cfg.MediaCache.MaxDiskMB << 20,
		MaxObjectBytes: cfg.MediaCache.MaxObjectMB << 20, CenterURL: cfg.CenterURL,
		APIToken: cfg.APIToken, NodeID: cfg.NodeID,
	})
	if err != nil {
		panic(err)
	}
	defer func() { _ = mediaCache.Close() }()
	availability := &printerAvailability{printers: make(map[string]struct{})}
	reg := registry.New(registry.Config{CenterURL: cfg.CenterURL, APIToken: cfg.APIToken, NodeID: cfg.NodeID, NodeName: cfg.NodeName, LANBaseURL: cfg.LANBaseURL, Version: version.Version, APIVersion: version.APIVersion, CacheMode: cfg.Cache.Mode, NamespaceID: cfg.NamespaceID, Capabilities: []string{"proxy", "adaptive_cache", "catalog_cache", "catalog_media_cache", "box_label_catalog", "local_print", "print_templates", "batch_print_v1"}, Inventory: func() interface{} { return printInventory(printer.Templates(), cfg.DefaultPrinter, availability) }, HeartbeatInterval: time.Duration(cfg.HeartbeatSeconds) * time.Second, OnCatalogRevision: catalogManager.OnRemoteRevision, OnTicketPublicKey: catalogManager.SetTicketPublicKey})
	ctx, cancel := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer cancel()
	startServiceAdvertisement(ctx, cfg)
	reg.Start(ctx)
	catalogManager.Start(ctx)
	startRemotePrintQueue(ctx, reg, printer, availability, time.Duration(cfg.PrintPollSeconds)*time.Second)
	startLocalPrintAuditSync(ctx, reg, printer, 5*time.Second)
	startRemoteCompletionSync(ctx, reg, printer, time.Second)

	terminals := newTerminalStore()
	router := gin.Default()
	router.Use(cors())
	router.GET("/edge/health", func(c *gin.Context) {
		catalogStatus, _ := catalogManager.Status()
		completionStatus, _ := printer.RemoteCompletionStatus()
		c.JSON(http.StatusOK, gin.H{"status": "ok", "mode": "proxy", "backend_version": version.Version, "backend_commit": version.Commit, "edge_api_version": version.APIVersion, "center": proxyHandler.CenterStatus(), "cache": responseCache.Status(), "catalog": catalogStatus, "catalog_media_cache": mediaCache.Status(), "catalog_ticket_key_ready": catalogManager.TicketKeyReady(), "registration": reg.Status(), "remote_print_completions": completionStatus})
	})
	router.GET("/edge/probe", func(c *gin.Context) {
		terminals.recordProbe(c.ClientIP(), c.Request.UserAgent(), c.GetHeader("X-Edge-Terminal-Name"))
		catalogStatus, _ := catalogManager.Status()
		c.JSON(http.StatusOK, gin.H{"ok": true, "node_id": cfg.NodeID, "node_name": cfg.NodeName, "lan_base_url": cfg.LANBaseURL, "capabilities": catalogCapabilities(catalogStatus), "catalog": catalogStatus, "catalog_ticket_key_ready": catalogManager.TicketKeyReady(), "direct_print_available": true, "cache_mode": cfg.Cache.Mode})
	})
	router.GET("/edge/label-print", serveLabelPrintPage)
	router.GET("/edge/web/label-templates", func(c *gin.Context) {
		c.JSON(http.StatusOK, gin.H{"templates": availableLabelTemplates(printer.Templates(), availability)})
	})
	router.GET("/edge/print-inventory", func(c *gin.Context) {
		terminals.recordProbe(c.ClientIP(), c.Request.UserAgent(), c.GetHeader("X-Edge-Terminal-Name"))
		c.JSON(http.StatusOK, printInventory(printer.Templates(), cfg.DefaultPrinter, availability))
	})
	router.GET("/edge/terminals/connect", func(c *gin.Context) {
		if err := catalogManager.AuthorizeTicket(c.GetHeader("X-Edge-Ticket")); err != nil {
			c.AbortWithStatusJSON(http.StatusUnauthorized, gin.H{"code": "EDGE_TERMINAL_TICKET_REQUIRED"})
			return
		}
		ip, userAgent := c.ClientIP(), c.Request.UserAgent()
		server := websocket.Server{
			Handshake: func(*websocket.Config, *http.Request) error { return nil },
			Handler:   func(ws *websocket.Conn) { terminals.connect(ws, ip, userAgent) },
		}
		server.ServeHTTP(c.Writer, c.Request)
	})
	router.POST("/edge/web/label-jobs", func(c *gin.Context) {
		createWebLabelJob(c, printer, availability)
	})
	// Catalog GETs may be served from the active local generation. Other /api/*
	// requests remain transparent center proxy requests.
	router.POST("/edge/print-jobs/direct", func(c *gin.Context) {
		createDirectPrintJob(c, printer, availability, reg.AuthorizeMobilePrint)
	})
	router.GET("/api/products", func(c *gin.Context) {
		serveCatalogProducts(c, catalogManager, proxyHandler)
	})
	router.GET("/api/skus", func(c *gin.Context) {
		serveCatalogSKUs(c, catalogManager, proxyHandler)
	})
	router.GET("/api/skus/:id", func(c *gin.Context) {
		serveCatalogSKU(c, catalogManager, proxyHandler)
	})
	router.GET("/edge/v2/catalog/media/:entity/:id/thumbnail", func(c *gin.Context) {
		serveCatalogMedia(c, catalogManager, mediaCache, cfg.NamespaceID)
	})
	router.GET("/api/box-labels", func(c *gin.Context) {
		serveCatalogBoxLabels(c, catalogManager, proxyHandler, centerRecentlyReachable(proxyHandler, reg))
	})
	router.GET("/api/box-labels/:id/resolve", func(c *gin.Context) {
		serveCatalogBoxLabelResolve(c, catalogManager, proxyHandler, centerRecentlyReachable(proxyHandler, reg))
	})
	router.GET("/api/box-labels/:id", func(c *gin.Context) {
		serveCatalogBoxLabel(c, catalogManager, proxyHandler, centerRecentlyReachable(proxyHandler, reg))
	})

	admin := router.Group("/edge", adminMiddleware(cfg.AdminToken))
	admin.GET("/capabilities", func(c *gin.Context) {
		catalogStatus, _ := catalogManager.Status()
		c.JSON(http.StatusOK, gin.H{"capabilities": catalogCapabilities(catalogStatus)})
	})
	admin.GET("/catalog/status", func(c *gin.Context) {
		status, err := catalogManager.Status()
		if err != nil {
			c.JSON(http.StatusInternalServerError, gin.H{"code": "CATALOG_STATUS_FAILED"})
			return
		}
		c.JSON(http.StatusOK, gin.H{"catalog": status})
	})
	admin.POST("/catalog/refresh", func(c *gin.Context) {
		go func() { _ = catalogManager.RefreshFull(context.Background()) }()
		c.JSON(http.StatusAccepted, gin.H{"status": "syncing"})
	})
	admin.GET("/catalog/products/search", func(c *gin.Context) {
		page, ok := readCatalogPage(c)
		if !ok {
			return
		}
		items, total, err := catalogManager.SearchProductsPage(c.Query("keyword"), page)
		if err != nil {
			c.JSON(http.StatusInternalServerError, gin.H{"code": "CATALOG_SEARCH_FAILED"})
			return
		}
		status, _ := catalogManager.Status()
		c.JSON(http.StatusOK, gin.H{"items": items, "total": total, "page": page.Page, "page_size": page.PageSize, "catalog": status, "source": "local"})
	})
	admin.POST("/catalog/products/cache", func(c *gin.Context) {
		var payload struct {
			Items []catalog.Product `json:"items"`
		}
		if err := c.ShouldBindJSON(&payload); err != nil || len(payload.Items) == 0 || len(payload.Items) > 100 {
			c.JSON(http.StatusBadRequest, gin.H{"code": "INVALID_PRODUCT_CACHE"})
			return
		}
		if err := catalogManager.CacheProducts(payload.Items); err != nil {
			c.JSON(http.StatusConflict, gin.H{"code": "PRODUCT_CACHE_NOT_READY"})
			return
		}
		c.JSON(http.StatusOK, gin.H{"cached": len(payload.Items)})
	})
	admin.GET("/catalog/skus/search", func(c *gin.Context) {
		page, ok := readCatalogPage(c)
		if !ok {
			return
		}
		var productID *uint
		if raw := strings.TrimSpace(c.Query("product_id")); raw != "" {
			parsed, err := strconv.ParseUint(raw, 10, 32)
			if err != nil {
				c.JSON(http.StatusBadRequest, gin.H{"code": "INVALID_PRODUCT_ID"})
				return
			}
			value := uint(parsed)
			productID = &value
		}
		items, total, err := catalogManager.SearchSKUsPage(c.Query("keyword"), productID, page)
		if err != nil {
			c.JSON(http.StatusInternalServerError, gin.H{"code": "CATALOG_SEARCH_FAILED"})
			return
		}
		status, _ := catalogManager.Status()
		c.JSON(http.StatusOK, gin.H{"items": items, "total": total, "page": page.Page, "page_size": page.PageSize, "catalog": status, "source": "local"})
	})
	admin.POST("/catalog/skus/cache", func(c *gin.Context) {
		var payload struct {
			Items []catalog.SKU `json:"items"`
		}
		if err := c.ShouldBindJSON(&payload); err != nil || len(payload.Items) == 0 || len(payload.Items) > 100 {
			c.JSON(http.StatusBadRequest, gin.H{"code": "INVALID_SKU_CACHE"})
			return
		}
		if err := catalogManager.CacheSKUs(payload.Items); err != nil {
			c.JSON(http.StatusConflict, gin.H{"code": "SKU_CACHE_NOT_READY"})
			return
		}
		c.JSON(http.StatusOK, gin.H{"cached": len(payload.Items)})
	})
	admin.GET("/catalog/box-labels/search", func(c *gin.Context) {
		filter, ok := readBoxLabelFilter(c)
		if !ok {
			return
		}
		items, total, err := catalogManager.SearchBoxLabels(filter)
		if err != nil {
			c.JSON(http.StatusInternalServerError, gin.H{"code": "BOX_LABEL_SEARCH_FAILED"})
			return
		}
		status, _ := catalogManager.Status()
		c.JSON(http.StatusOK, gin.H{"items": items, "total": total, "page": filter.Page, "page_size": filter.PageSize, "catalog": status, "source": "local"})
	})
	admin.GET("/catalog/box-labels/:id", func(c *gin.Context) {
		id, err := strconv.ParseUint(strings.TrimSpace(c.Param("id")), 10, 32)
		if err != nil || id == 0 {
			c.JSON(http.StatusBadRequest, gin.H{"code": "INVALID_BOX_LABEL_ID"})
			return
		}
		item, err := catalogManager.GetBoxLabel(uint(id))
		if err != nil {
			c.JSON(http.StatusNotFound, gin.H{"code": "BOX_LABEL_NOT_FOUND"})
			return
		}
		status, _ := catalogManager.Status()
		c.JSON(http.StatusOK, gin.H{"item": item, "catalog": status, "source": "local"})
	})
	admin.POST("/catalog/box-labels/cache", func(c *gin.Context) {
		var payload struct {
			Items []catalog.BoxLabel `json:"items"`
		}
		if err := c.ShouldBindJSON(&payload); err != nil || len(payload.Items) == 0 || len(payload.Items) > 100 {
			c.JSON(http.StatusBadRequest, gin.H{"code": "INVALID_BOX_LABEL_CACHE"})
			return
		}
		if err := catalogManager.CacheBoxLabels(payload.Items); err != nil {
			c.JSON(http.StatusConflict, gin.H{"code": "BOX_LABEL_CACHE_NOT_READY"})
			return
		}
		c.JSON(http.StatusOK, gin.H{"cached": len(payload.Items)})
	})
	admin.GET("/cache/status", func(c *gin.Context) {
		c.JSON(http.StatusOK, gin.H{"cache": responseCache.Status(), "center": proxyHandler.CenterStatus(), "whitelist": cfg.Cache.Whitelist})
	})
	admin.POST("/cache/clear", func(c *gin.Context) { c.JSON(http.StatusOK, gin.H{"cleared": responseCache.Clear()}) })
	admin.GET("/terminals", func(c *gin.Context) { c.JSON(http.StatusOK, gin.H{"terminals": terminals.list()}) })
	admin.POST("/terminals/:terminal_id/find", func(c *gin.Context) {
		result, err := terminals.find(c.Param("terminal_id"))
		writeTerminalCommandResponse(c, result, err)
	})
	admin.POST("/terminals/:terminal_id/find/stop", func(c *gin.Context) {
		result, err := terminals.stopFind(c.Param("terminal_id"))
		writeTerminalCommandResponse(c, result, err)
	})
	admin.GET("/print-config", func(c *gin.Context) { c.JSON(http.StatusOK, printer.Config()) })
	admin.PUT("/print-inventory", func(c *gin.Context) {
		var payload struct {
			Printers []string `json:"printers"`
		}
		if err := c.ShouldBindJSON(&payload); err != nil {
			c.JSON(http.StatusBadRequest, gin.H{"code": "INVALID_PRINT_INVENTORY"})
			return
		}
		availability.Set(payload.Printers)
		go reg.SyncNow(context.Background())
		c.JSON(http.StatusOK, gin.H{"printers": len(payload.Printers)})
	})
	router.GET("/edge/print-templates", func(c *gin.Context) { c.JSON(http.StatusOK, gin.H{"templates": printer.Templates()}) })
	admin.POST("/print-templates", func(c *gin.Context) {
		var template printing.Template
		if err := c.ShouldBindJSON(&template); err != nil || template.ID == "" || template.Name == "" {
			c.JSON(http.StatusBadRequest, gin.H{"code": "INVALID_PRINT_TEMPLATE"})
			return
		}
		if err := printer.SaveTemplate(template); err != nil {
			c.JSON(http.StatusInternalServerError, gin.H{"code": "PRINT_TEMPLATE_SAVE_FAILED"})
			return
		}
		c.JSON(http.StatusOK, gin.H{"template": template})
	})
	admin.PUT("/print-templates/:id", func(c *gin.Context) {
		var template printing.Template
		if err := c.ShouldBindJSON(&template); err != nil {
			c.JSON(http.StatusBadRequest, gin.H{"code": "INVALID_PRINT_TEMPLATE"})
			return
		}
		template.ID = c.Param("id")
		if err := printer.SaveTemplate(template); err != nil {
			c.JSON(http.StatusInternalServerError, gin.H{"code": "PRINT_TEMPLATE_SAVE_FAILED"})
			return
		}
		c.JSON(http.StatusOK, gin.H{"template": template})
	})
	admin.DELETE("/print-templates/:id", func(c *gin.Context) {
		if err := printer.DeleteTemplate(c.Param("id")); err != nil {
			c.JSON(http.StatusInternalServerError, gin.H{"code": "PRINT_TEMPLATE_DELETE_FAILED"})
			return
		}
		c.Status(http.StatusNoContent)
	})
	router.GET("/edge/print-jobs", func(c *gin.Context) {
		page, _ := strconv.Atoi(c.DefaultQuery("page", "1"))
		pageSize, _ := strconv.Atoi(c.DefaultQuery("page_size", "50"))
		jobs, total := printer.ListJobs(c.Query("status"), page, pageSize)
		c.JSON(http.StatusOK, gin.H{"jobs": jobs, "total": total, "page": page, "page_size": pageSize})
	})
	admin.POST("/print-jobs/pull", func(c *gin.Context) {
		claimed, err := claimRemotePrintJob(c.Request.Context(), reg, printer, availability)
		if err != nil {
			c.JSON(http.StatusBadGateway, gin.H{"code": "CENTER_PRINT_JOB_PULL_FAILED"})
			return
		}
		c.JSON(http.StatusOK, gin.H{"claimed": claimed})
	})
	router.POST("/edge/print-jobs", func(c *gin.Context) {
		createPrintJob(c, printer, availability)
	})
	router.GET("/edge/print-jobs/:id", func(c *gin.Context) {
		job, ok := printer.Find(c.Param("id"))
		if !ok {
			c.JSON(http.StatusNotFound, gin.H{"code": "PRINT_JOB_NOT_FOUND"})
			return
		}
		c.JSON(http.StatusOK, gin.H{"job": job})
	})
	admin.POST("/print-jobs/:id/retry", func(c *gin.Context) {
		job, ok, err := printer.SetStatus(c.Param("id"), "queued")
		if err != nil {
			c.JSON(http.StatusInternalServerError, gin.H{"code": "PRINT_JOB_PERSIST_FAILED"})
			return
		}
		if !ok {
			c.JSON(http.StatusNotFound, gin.H{"code": "PRINT_JOB_NOT_FOUND"})
			return
		}
		c.JSON(http.StatusOK, gin.H{"job": job})
	})
	admin.POST("/print-jobs/:id/cancel", func(c *gin.Context) {
		job, ok, err := printer.SetStatus(c.Param("id"), "cancelled")
		if err != nil {
			c.JSON(http.StatusInternalServerError, gin.H{"code": "PRINT_JOB_PERSIST_FAILED"})
			return
		}
		if !ok {
			c.JSON(http.StatusNotFound, gin.H{"code": "PRINT_JOB_NOT_FOUND"})
			return
		}
		c.JSON(http.StatusOK, gin.H{"job": job})
	})
	admin.POST("/print-jobs/:id/status", func(c *gin.Context) {
		var payload struct {
			Status string `json:"status"`
			Error  string `json:"error"`
		}
		if err := c.ShouldBindJSON(&payload); err != nil || !validJobStatus(payload.Status) {
			c.JSON(http.StatusBadRequest, gin.H{"code": "INVALID_PRINT_JOB_STATUS"})
			return
		}
		job, ok, err := printer.SetStatusWithError(c.Param("id"), payload.Status, payload.Error)
		if err != nil {
			c.JSON(http.StatusInternalServerError, gin.H{"code": "PRINT_JOB_PERSIST_FAILED"})
			return
		}
		if !ok {
			c.JSON(http.StatusNotFound, gin.H{"code": "PRINT_JOB_NOT_FOUND"})
			return
		}
		if job.RemoteJobID > 0 && isTerminalRemoteStatus(payload.Status) {
			_ = syncRemoteCompletions(c.Request.Context(), reg, printer)
		}
		c.JSON(http.StatusOK, gin.H{"job": job})
	})
	router.NoRoute(func(c *gin.Context) {
		if strings.HasPrefix(c.Request.URL.Path, "/api/") || c.Request.URL.Path == "/api" {
			proxyHandler.ServeHTTP(c.Writer, c.Request)
			return
		}
		c.JSON(http.StatusNotFound, gin.H{"code": "NOT_FOUND"})
	})

	server := &http.Server{Addr: ":" + cfg.Port, Handler: router, ReadHeaderTimeout: 10 * time.Second}
	go func() {
		<-ctx.Done()
		shutdown, done := context.WithTimeout(context.Background(), 5*time.Second)
		defer done()
		_ = server.Shutdown(shutdown)
	}()
	fmt.Printf("FSCM edge proxy %s listening on %s\n", version.Version, server.Addr)
	if err := server.ListenAndServe(); err != nil && err != http.ErrServerClosed {
		panic(err)
	}
}

func startServiceAdvertisement(ctx context.Context, cfg *config.Config) {
	port, err := strconv.Atoi(cfg.Port)
	if err != nil || port < 1 || port > 65535 {
		fmt.Printf("FSCM edge discovery disabled: invalid port %q\n", cfg.Port)
		return
	}
	server, err := zeroconf.Register(
		cfg.NodeID,
		"_fscm-edge._tcp",
		"local.",
		port,
		[]string{
			"scheme=http",
			"node_id=" + cfg.NodeID,
			"lan_base_url=" + cfg.LANBaseURL,
		},
		nil,
	)
	if err != nil {
		fmt.Printf("FSCM edge discovery advertisement failed: %v\n", err)
		return
	}
	fmt.Printf("FSCM edge discovery advertising %s on port %d\n", cfg.NodeID, port)
	go func() {
		<-ctx.Done()
		server.Shutdown()
	}()
}

func readBoxLabelFilter(c *gin.Context) (catalog.BoxLabelFilter, bool) {
	filter := catalog.BoxLabelFilter{Keyword: strings.TrimSpace(c.Query("keyword")), ConsolidationOrderCode: strings.TrimSpace(c.Query("consolidation_order_code")), StatusGroup: strings.TrimSpace(c.Query("status_group")), ReceivingStatus: strings.TrimSpace(c.Query("receiving_status"))}
	filter.Page, _ = strconv.Atoi(c.DefaultQuery("page", "1"))
	filter.PageSize, _ = strconv.Atoi(c.DefaultQuery("page_size", "20"))
	if filter.Page < 1 {
		filter.Page = 1
	}
	if filter.PageSize < 1 || filter.PageSize > 100 {
		filter.PageSize = 20
	}
	values := []struct {
		key    string
		target *uint
	}{{"product_id", &filter.ProductID}, {"sku_id", &filter.SKUID}, {"consolidation_order_id", &filter.ConsolidationOrderID}}
	for _, value := range values {
		raw := strings.TrimSpace(c.Query(value.key))
		if raw == "" {
			continue
		}
		parsed, err := strconv.ParseUint(raw, 10, 32)
		if err != nil || parsed == 0 {
			c.JSON(http.StatusBadRequest, gin.H{"code": "INVALID_" + strings.ToUpper(value.key)})
			return catalog.BoxLabelFilter{}, false
		}
		*value.target = uint(parsed)
	}
	return filter, true
}

func readCatalogPage(c *gin.Context) (catalog.PageFilter, bool) {
	page, err := strconv.Atoi(c.DefaultQuery("page", "1"))
	if err != nil || page < 1 {
		c.JSON(http.StatusBadRequest, gin.H{"code": "INVALID_PAGE"})
		return catalog.PageFilter{}, false
	}
	pageSize, err := strconv.Atoi(c.DefaultQuery("page_size", "20"))
	if err != nil || pageSize < 1 || pageSize > 100 {
		c.JSON(http.StatusBadRequest, gin.H{"code": "INVALID_PAGE_SIZE"})
		return catalog.PageFilter{}, false
	}
	return catalog.PageFilter{Page: page, PageSize: pageSize}, true
}

func createPrintJob(c *gin.Context, printer *printing.Service, availability *printerAvailability) {
	var req printing.Request
	if err := c.ShouldBindJSON(&req); err != nil {
		c.JSON(http.StatusBadRequest, gin.H{"code": "INVALID_PRINT_JOB"})
		return
	}
	if req.TemplateID != "" && !printer.HasTemplate(req.TemplateID) {
		c.JSON(http.StatusBadRequest, gin.H{"code": "PRINT_TEMPLATE_NOT_FOUND"})
		return
	}
	if template, ok := findPrintTemplate(printer.Templates(), req.TemplateID); ok {
		if _, message := validateLabelDisplayText(req, template); message != "" {
			c.JSON(http.StatusBadRequest, gin.H{"code": "LABEL_TEXT_TOO_LONG", "message": message})
			return
		}
	}
	if code := validateManufacturerBoxMarkRequest(req, printer.Templates(), availability); code != "" {
		c.JSON(http.StatusBadRequest, gin.H{"code": code})
		return
	}
	printerName := resolvePrintRequestPrinter(req, printer)
	if printerName == "" || !availability.Has(printerName) {
		c.JSON(http.StatusConflict, gin.H{"code": "PRINTER_UNAVAILABLE", "printer": printerName})
		return
	}
	job, duplicate, err := printer.Create(req)
	if err != nil {
		if errors.Is(err, printing.ErrPrintCopiesExceeded) {
			c.JSON(http.StatusBadRequest, gin.H{"code": "PRINT_COPIES_LIMIT_EXCEEDED", "message": err.Error()})
			return
		}
		c.JSON(http.StatusInternalServerError, gin.H{"code": "PRINT_JOB_PERSIST_FAILED"})
		return
	}
	c.JSON(http.StatusAccepted, gin.H{"status": "accepted", "duplicate": duplicate, "job": job})
}

func resolvePrintRequestPrinter(req printing.Request, printer *printing.Service) string {
	if req.TemplateID != "" {
		for _, template := range printer.Templates() {
			if template.ID == req.TemplateID {
				return strings.TrimSpace(first(template.Printer, printer.Config().DefaultPrinter))
			}
		}
	}
	return strings.TrimSpace(first(req.Printer, printer.Config().DefaultPrinter))
}

func findPrintTemplate(templates []printing.Template, id string) (printing.Template, bool) {
	for _, template := range templates {
		if template.ID == id {
			return template, true
		}
	}
	return printing.Template{}, false
}

func restrictedLabelTextMessage(template printing.Template) string {
	switch normalizeTemplateLayoutStyle(template) {
	case "location_code_quad_qr":
		return "库位码最多 12 个字符，当前内容不适配，请输入少于或等于 12 个字符。"
	case "qr_left_text_right":
		return "左右排版右侧文字最多 12 个字符，当前内容不适配，请输入少于或等于 12 个字符。"
	default:
		return ""
	}
}

func validateRestrictedLabelText(template printing.Template, text string) string {
	message := restrictedLabelTextMessage(template)
	if message == "" || utf8.RuneCountInString(text) <= 12 {
		return ""
	}
	return message
}

func validateLabelDisplayText(req printing.Request, template printing.Template) (string, string) {
	if restrictedLabelTextMessage(template) == "" {
		return "", ""
	}
	if isTextLabelKind(req.Kind) {
		text := strings.TrimSpace(req.Text)
		return text, validateRestrictedLabelText(template, text)
	}
	for _, item := range req.Items {
		text := strings.TrimSpace(item.QRCodeContent)
		if text == "" {
			text = first(template.SkuQRPrefix, "T") + strings.TrimSpace(item.SKUCode)
		}
		if message := validateRestrictedLabelText(template, text); message != "" {
			return text, message
		}
	}
	return "", ""
}

func isTextLabelKind(kind string) bool {
	kind = strings.ToLower(strings.TrimSpace(kind))
	return kind == "manual_text" || kind == "batch_content"
}

func validateManufacturerBoxMarkRequest(req printing.Request, templates []printing.Template, availability *printerAvailability) string {
	if req.Kind != "manufacturer_box_mark" {
		return ""
	}
	if len(req.BoxMarks) == 0 || len(req.BoxMarks) > 100 {
		return "INVALID_BOX_MARK_COUNT"
	}
	if req.Copies < 1 || req.Copies > printing.MaxCopiesPerContent {
		return "INVALID_PRINT_COPIES"
	}
	for _, template := range templates {
		if template.ID != req.TemplateID {
			continue
		}
		if template.Type != "manufacturer_box_mark" || math.Abs(template.WidthMillimeters-100) >= 0.1 || math.Abs(template.HeightMillimeters-150) >= 0.1 || !strings.EqualFold(strings.TrimSpace(template.Orientation), "portrait") {
			return "INVALID_BOX_MARK_TEMPLATE"
		}
		if normalizeTemplateLayoutStyle(template) == "box_mark_quad_qr" {
			if boxMarkDocumentVersion(req) != "manufacturer_box_mark.v2" {
				return "INVALID_BOX_MARK_PAYLOAD"
			}
			for _, mark := range req.BoxMarks {
				if !validQuadBoxMark(mark) {
					return "INVALID_BOX_MARK_SKU_CONTENT"
				}
			}
		}
		if strings.TrimSpace(template.Printer) == "" || !availability.Has(template.Printer) {
			return "BOX_MARK_PRINTER_UNAVAILABLE"
		}
		return ""
	}
	return "PRINT_TEMPLATE_NOT_FOUND"
}

func boxMarkDocumentVersion(req printing.Request) string {
	if len(req.PayloadSnapshot) == 0 {
		return ""
	}
	var payload struct {
		DocumentVersion string `json:"document_version"`
	}
	if json.Unmarshal(req.PayloadSnapshot, &payload) != nil {
		return ""
	}
	return strings.TrimSpace(payload.DocumentVersion)
}

func validQuadBoxMark(mark printing.BoxMark) bool {
	boxMarkCode := first(mark.Shop, mark.BoxUID, mark.InboundCode)
	boxQRPayload := first(mark.BoxQRPayload, mark.BoxUID, mark.InboundCode, boxMarkCode)
	skuQRPayload := first(mark.SKUQRPayload, mark.SKUCode)
	return boxMarkCode != "" && boxQRPayload != "" && strings.TrimSpace(mark.SKUCode) != "" &&
		strings.TrimSpace(mark.SKUName) != "" && skuQRPayload != "" && mark.QtyPerBox > 0
}

func printInventory(templates []printing.Template, defaultPrinter string, availability *printerAvailability) map[string]interface{} {
	printerSet := make(map[string]bool)
	printers := make([]map[string]interface{}, 0, len(templates)+1)
	addPrinter := func(name string) {
		name = strings.TrimSpace(name)
		if name == "" || printerSet[name] {
			return
		}
		printerSet[name] = true
		printers = append(printers, map[string]interface{}{"name": name, "is_default": name == strings.TrimSpace(defaultPrinter), "status": "ready"})
	}
	addPrinter(defaultPrinter)
	ordered := orderPrintTemplates(templates)
	result := make([]map[string]interface{}, 0, len(ordered))
	for _, template := range ordered {
		if !availability.Has(template.Printer) {
			continue
		}
		addPrinter(template.Printer)
		result = append(result, map[string]interface{}{
			"code": template.ID, "number": template.TemplateNumber, "name": template.Name, "sort_order": template.SortOrder, "type": inventoryTemplateType(template), "printer_name": template.Printer,
			"width_mm": template.WidthMillimeters, "height_mm": template.HeightMillimeters, "orientation": template.Orientation,
			"layout_style": normalizeTemplateLayoutStyle(template), "text_font_size_pt": templateTextFontSize(template),
			"max_display_length": normalizedMaxDisplayLength(template), "label_qr_prefix": template.LabelQRPrefix,
			"version": templateVersion(template), "available": true,
		})
	}
	return map[string]interface{}{"schema_version": 2, "printers": printers, "templates": result}
}

func normalizeTemplateLayoutStyle(template printing.Template) string {
	if strings.EqualFold(strings.TrimSpace(template.LayoutStyle), "location_code_quad_qr") {
		return "location_code_quad_qr"
	}
	if strings.EqualFold(strings.TrimSpace(template.LayoutStyle), "qr_left_text_right") {
		return "qr_left_text_right"
	}
	if strings.EqualFold(strings.TrimSpace(template.LayoutStyle), "box_mark_quad_qr") {
		return "box_mark_quad_qr"
	}
	return "stacked"
}

func templateTextFontSize(template printing.Template) float64 {
	if template.TextFontSizePoints > 0 {
		return template.TextFontSizePoints
	}
	if matchesTemplateSize(template, 60, 40) {
		if normalizeTemplateLayoutStyle(template) == "qr_left_text_right" {
			return 18
		}
		return 16
	}
	if normalizeTemplateLayoutStyle(template) == "location_code_quad_qr" {
		return 28
	}
	return 10
}

func normalizedMaxDisplayLength(template printing.Template) int {
	if restrictedLabelTextMessage(template) != "" {
		return 12
	}
	if template.MaxDisplayLength > 0 {
		return template.MaxDisplayLength
	}
	return 16
}

func templateVersion(template printing.Template) string {
	canonical := strings.Join([]string{
		strings.TrimSpace(template.ID), strings.ToLower(strings.TrimSpace(template.Type)), strings.TrimSpace(template.Printer),
		strconv.FormatFloat(template.WidthMillimeters, 'f', 3, 64), strconv.FormatFloat(template.HeightMillimeters, 'f', 3, 64),
		strings.ToLower(strings.TrimSpace(template.Orientation)), strings.ToLower(strings.TrimSpace(template.Mode)), strconv.Itoa(template.Copies),
		strconv.FormatFloat(template.OffsetXMillimeters, 'f', 3, 64), template.SkuQRPrefix, template.LabelQRPrefix,
		normalizeTemplateLayoutStyle(template), strconv.FormatFloat(templateTextFontSize(template), 'f', 2, 64), strconv.Itoa(normalizedMaxDisplayLength(template)),
	}, "|")
	sum := sha256.Sum256([]byte(canonical))
	return hex.EncodeToString(sum[:6])
}

func orderPrintTemplates(templates []printing.Template) []printing.Template {
	ordered := append([]printing.Template(nil), templates...)
	sort.SliceStable(ordered, func(left, right int) bool {
		leftOrder := ordered[left].SortOrder
		rightOrder := ordered[right].SortOrder
		if leftOrder <= 0 && rightOrder <= 0 {
			return false
		}
		if leftOrder <= 0 {
			return false
		}
		if rightOrder <= 0 {
			return true
		}
		return leftOrder < rightOrder
	})
	return ordered
}

func matchesTemplateSize(template printing.Template, width, height float64) bool {
	return math.Abs(template.WidthMillimeters-width) <= 0.1 && math.Abs(template.HeightMillimeters-height) <= 0.1
}

func inventoryTemplateType(template printing.Template) string {
	// Template purpose is an explicit user setting. Paper size does not define it:
	// a 100 x 150 mm template may be a shipping label, a generic label, or a box mark.
	return first(strings.ToLower(strings.TrimSpace(template.Type)), "label")
}

func startRemotePrintQueue(ctx context.Context, reg *registry.Client, printer *printing.Service, availability *printerAvailability, interval time.Duration) {
	if interval < time.Second {
		interval = 5 * time.Second
	}
	go func() {
		ticker := time.NewTicker(interval)
		defer ticker.Stop()
		for {
			select {
			case <-ctx.Done():
				return
			case <-ticker.C:
				_, _ = claimRemotePrintJob(ctx, reg, printer, availability)
			}
		}
	}()
}

func startLocalPrintAuditSync(ctx context.Context, reg *registry.Client, printer *printing.Service, interval time.Duration) {
	go func() {
		ticker := time.NewTicker(interval)
		defer ticker.Stop()
		for {
			_ = syncLocalPrintAudits(ctx, reg, printer)
			select {
			case <-ctx.Done():
				return
			case <-ticker.C:
			}
		}
	}()
}

func startRemoteCompletionSync(ctx context.Context, reg *registry.Client, printer *printing.Service, interval time.Duration) {
	go func() {
		ticker := time.NewTicker(interval)
		defer ticker.Stop()
		for {
			_ = syncRemoteCompletions(ctx, reg, printer)
			select {
			case <-ctx.Done():
				return
			case <-ticker.C:
			}
		}
	}()
}

func syncRemoteCompletions(ctx context.Context, reg *registry.Client, printer *printing.Service) error {
	completions, err := printer.PendingRemoteCompletions(50)
	if err != nil {
		return err
	}
	for _, completion := range completions {
		var result interface{}
		if len(completion.Result) > 0 {
			result = json.RawMessage(completion.Result)
		}
		err = reg.CompletePrintJobWithCode(ctx, completion.RemoteJobID, completion.LeaseToken, completion.Status, completion.ErrorCode, completion.ErrorMessage, result)
		if err == nil || registry.IsLeaseInvalid(err) {
			if markErr := printer.MarkRemoteCompletionSynced(completion.RemoteJobID); markErr != nil {
				return markErr
			}
			continue
		}
		delay := time.Duration(1<<min(completion.Attempts, 4)) * time.Second
		if markErr := printer.MarkRemoteCompletionFailed(completion.RemoteJobID, err, time.Now().Add(delay)); markErr != nil {
			return markErr
		}
		return err
	}
	return nil
}

func syncLocalPrintAudits(ctx context.Context, reg *registry.Client, printer *printing.Service) error {
	jobs, err := printer.PendingAudits(50)
	if err != nil {
		return err
	}
	for _, job := range jobs {
		contentSnapshot := interface{}(map[string]interface{}{
			"text": job.Text, "qr_code_content": job.QRCodeContent, "items": job.Items, "box_marks": job.BoxMarks,
		})
		if len(job.PayloadSnapshot) > 0 {
			contentSnapshot = json.RawMessage(job.PayloadSnapshot)
		}
		audit := registry.LocalPrintAudit{
			LocalJobID: job.ID, Source: job.Source, TemplateCode: job.TemplateID, PrinterName: job.Printer,
			JobType: first(job.Kind, "local_print"), Status: centerLocalPrintStatus(job.Status), LocalStatus: job.Status, Copies: job.Copies,
			ContentSnapshot: contentSnapshot,
			SubmittedAt:     job.SubmittedAt, StartedAt: job.StartedAt, FinishedAt: job.FinishedAt, ErrorMessage: job.Error,
		}
		if err := reg.SyncLocalPrintAudit(ctx, audit); err != nil {
			_ = printer.MarkAuditFailed(job.ID, err)
			return err
		}
		if err := printer.MarkAuditSynced(job.ID); err != nil {
			return err
		}
	}
	return nil
}

func centerLocalPrintStatus(status string) string {
	switch strings.ToLower(strings.TrimSpace(status)) {
	case "completed", "done", "success", "succeeded":
		return "succeeded"
	case "failed", "cancelled":
		return "failed"
	case "interrupted", "unknown":
		return "unknown"
	case "printing":
		return "printing"
	default:
		return "queued"
	}
}

func claimRemotePrintJob(ctx context.Context, reg *registry.Client, printer *printing.Service, availability *printerAvailability) (bool, error) {
	if !availability.Ready() {
		return false, nil
	}
	if !printer.BeginRemoteClaim() {
		return false, nil
	}
	defer printer.EndRemoteClaim()
	claim, err := reg.ClaimPrintJob(ctx)
	if err != nil || claim == nil {
		return false, err
	}
	var payload struct {
		Kind      string             `json:"kind"`
		SKUID     uint               `json:"sku_id"`
		SKUCode   string             `json:"sku_code"`
		QRPayload string             `json:"qr_payload"`
		Text      string             `json:"text"`
		Items     []printing.BoxMark `json:"items"`
	}
	if err := json.Unmarshal(claim.PayloadSnapshot, &payload); err != nil || !validClaimPayload(payload.Kind, payload.SKUCode, payload.Text, payload.Items) {
		return enqueueClaimFailure(printer, claim, "INVALID_PRINT_JOB_PAYLOAD", "invalid print job payload")
	}
	localID := fmt.Sprintf("center-job-%d", claim.ID)
	request := printing.Request{JobID: localID, IdempotencyKey: localID, Source: "center", Printer: claim.PrinterName, Template: claim.TemplateCode, TemplateID: claim.TemplateCode, Kind: payload.Kind, Text: payload.Text, QRCodeContent: first(payload.QRPayload, payload.Text), PayloadSnapshot: append(json.RawMessage(nil), claim.PayloadSnapshot...), Copies: claim.Copies, BoxMarks: payload.Items, RemoteBatchID: claim.BatchID, RemoteSequenceNo: claim.SequenceNo, RemoteJobType: claim.JobType, RemoteAttemptCount: claim.AttemptCount}
	if payload.Kind == "manual_text" {
		// Keep one item for older Windows workers that predate the manual_text
		// dispatcher. New workers render Text directly; older workers print this QR item.
		request.Items = []printing.Item{{SKUCode: payload.Text, QRCodeContent: payload.Text, Quantity: claim.Copies}}
	} else if payload.Kind != "manufacturer_box_mark" && payload.Kind != "batch_content" {
		request.Items = []printing.Item{{SKUID: payload.SKUID, SKUCode: payload.SKUCode, QRCodeContent: payload.QRPayload, Quantity: claim.Copies}}
	}
	printerName := resolvePrintRequestPrinter(request, printer)
	if code := validateManufacturerBoxMarkRequest(request, printer.Templates(), availability); code != "" {
		return enqueueClaimFailure(printer, claim, code, code)
	}
	if printerName == "" || !availability.Has(printerName) {
		message := "printer unavailable"
		if printerName != "" {
			message = fmt.Sprintf("printer unavailable: %s", printerName)
		}
		return enqueueClaimFailure(printer, claim, "PRINTER_UNAVAILABLE", message)
	}
	if template, ok := findPrintTemplate(printer.Templates(), request.TemplateID); ok {
		if _, message := validateLabelDisplayText(request, template); message != "" {
			return enqueueClaimFailure(printer, claim, "INVALID_LABEL_CONTENT", message)
		}
	}
	if _, _, err := printer.CreateRemote(request, claim.ID, claim.LeaseToken); err != nil {
		if errors.Is(err, printing.ErrPrintCopiesExceeded) {
			return enqueueClaimFailure(printer, claim, "PRINT_COPIES_LIMIT_EXCEEDED", err.Error())
		}
		return false, err
	}
	return true, nil
}

func enqueueClaimFailure(printer *printing.Service, claim *registry.ClaimedPrintJob, errorCode, errorMessage string) (bool, error) {
	result := map[string]interface{}{"phase": "claim_validation", "batch_id": claim.BatchID, "sequence_no": claim.SequenceNo}
	if err := printer.EnqueueRemoteFailure(claim.ID, claim.LeaseToken, errorCode, errorMessage, result); err != nil {
		return false, err
	}
	return true, nil
}

func validClaimPayload(kind, skuCode, text string, items []printing.BoxMark) bool {
	switch kind {
	case "manual_text", "batch_content":
		return strings.TrimSpace(text) != ""
	case "manufacturer_box_mark":
		return len(items) > 0
	default:
		return strings.TrimSpace(skuCode) != ""
	}
}

func isTerminalRemoteStatus(status string) bool {
	return status == "completed" || status == "failed" || status == "cancelled" || status == "interrupted"
}

func validJobStatus(status string) bool {
	switch status {
	case "queued", "printing", "completed", "failed", "cancelled":
		return true
	default:
		return false
	}
}

func serveCatalogProducts(c *gin.Context, catalogManager *catalog.Manager, proxyHandler *edgeproxy.Handler) {
	if !catalogRequestAuthorized(c, catalogManager, proxyHandler) {
		return
	}
	page, ok := readCatalogPage(c)
	if !ok {
		return
	}
	localCompatible := c.Query("region") == "" && c.Query("manufacturer_id") == "" &&
		c.Query("product_id") == "" && strings.EqualFold(c.DefaultQuery("match_mode", "prefix"), "prefix")
	status, err := catalogManager.Status()
	if err == nil && status.Ready && localCompatible {
		items, total, searchErr := catalogManager.SearchProductsPage(c.Query("keyword"), page)
		if searchErr == nil && (len(items) > 0 || strings.TrimSpace(c.Query("keyword")) == "") {
			writeCatalogPageState(c, items, total, page, "CATALOG-HIT")
			return
		}
	}
	items, total, err := catalogManager.FetchAndCacheProducts(c.Request.Context(), c.Request.URL.Query())
	if err != nil {
		c.AbortWithStatusJSON(http.StatusBadGateway, gin.H{"code": "EDGE_CATALOG_CENTER_FAILED", "msg": err.Error()})
		return
	}
	writeCatalogPageState(c, items, total, page, "CATALOG-FILL")
}

func serveCatalogSKUs(c *gin.Context, catalogManager *catalog.Manager, proxyHandler *edgeproxy.Handler) {
	if !catalogRequestAuthorized(c, catalogManager, proxyHandler) {
		return
	}
	if !isCompatibleCatalogSearch(c, true) {
		items, total, err := catalogManager.FetchAndCacheSKUs(c.Request.Context(), c.Request.URL.Query())
		if err != nil {
			c.AbortWithStatusJSON(http.StatusBadGateway, gin.H{"code": "EDGE_CATALOG_CENTER_FAILED", "msg": err.Error()})
			return
		}
		writeCatalogListState(c, items, total, "CATALOG-FILL")
		return
	}
	var productID *uint
	if raw := strings.TrimSpace(c.Query("product_id")); raw != "" {
		value, parseErr := strconv.ParseUint(raw, 10, 32)
		if parseErr != nil {
			proxyHandler.ServeHTTP(c.Writer, c.Request)
			return
		}
		parsed := uint(value)
		productID = &parsed
	}
	matchMode := strings.ToLower(strings.TrimSpace(c.DefaultQuery("match_mode", "prefix")))
	status, statusErr := catalogManager.Status()
	if statusErr == nil && status.Ready {
		items, _, searchErr := catalogManager.SearchSKUsPageMode(
			c.Query("keyword"),
			productID,
			matchMode,
			catalog.PageFilter{Page: 1, PageSize: 100},
		)
		if searchErr == nil && len(items) > 0 {
			writeCatalogList(c, items)
			return
		}
	}
	items, total, err := catalogManager.FetchAndCacheSKUs(c.Request.Context(), c.Request.URL.Query())
	if err != nil {
		c.AbortWithStatusJSON(http.StatusBadGateway, gin.H{"code": "EDGE_CATALOG_CENTER_FAILED", "msg": err.Error()})
		return
	}
	writeCatalogListState(c, items, total, "CATALOG-FILL")
}

func serveCatalogSKU(c *gin.Context, catalogManager *catalog.Manager, proxyHandler *edgeproxy.Handler) {
	if !catalogRequestAuthorized(c, catalogManager, proxyHandler) {
		return
	}
	idOrCode := strings.TrimSpace(c.Param("id"))
	if idOrCode == "" {
		c.AbortWithStatusJSON(http.StatusBadRequest, gin.H{"code": "INVALID_SKU"})
		return
	}
	status, statusErr := catalogManager.Status()
	if statusErr == nil && status.Ready {
		item, searchErr := catalogManager.GetSKU(idOrCode)
		if searchErr == nil && item != nil {
			writeCatalogObject(c, item, "CATALOG-HIT")
			return
		}
	}
	item, err := catalogManager.FetchAndCacheSKU(c.Request.Context(), idOrCode)
	if err != nil {
		c.AbortWithStatusJSON(http.StatusBadGateway, gin.H{"code": "EDGE_CATALOG_CENTER_FAILED", "msg": err.Error()})
		return
	}
	if item == nil {
		c.AbortWithStatusJSON(http.StatusNotFound, gin.H{"code": "SKU_NOT_FOUND", "msg": "SKU not found"})
		return
	}
	writeCatalogObject(c, item, "CATALOG-FILL")
}

func serveCatalogMedia(c *gin.Context, catalogManager *catalog.Manager, mediaCache *catalogmedia.Cache, namespaceID uint) {
	if err := catalogManager.AuthorizeTicket(c.GetHeader("X-Edge-Ticket")); err != nil {
		c.AbortWithStatusJSON(http.StatusUnauthorized, gin.H{"code": "EDGE_CATALOG_TICKET_REQUIRED"})
		return
	}
	entity := strings.ToLower(strings.TrimSpace(c.Param("entity")))
	if entity != "product" && entity != "sku" {
		c.AbortWithStatusJSON(http.StatusBadRequest, gin.H{"code": "EDGE_MEDIA_ENTITY_INVALID"})
		return
	}
	entityID, err := strconv.ParseUint(strings.TrimSpace(c.Param("id")), 10, 32)
	version := strings.TrimSpace(c.Query("v"))
	if err != nil || entityID == 0 || !validMediaVersion(version) {
		c.AbortWithStatusJSON(http.StatusBadRequest, gin.H{"code": "EDGE_MEDIA_REQUEST_INVALID"})
		return
	}
	result, err := mediaCache.Get(c.Request.Context(), namespaceID, entity, uint(entityID), version)
	if err != nil {
		c.AbortWithStatusJSON(http.StatusServiceUnavailable, gin.H{"code": "EDGE_MEDIA_NOT_CACHED", "msg": "图片尚未缓存且中心当前不可用"})
		return
	}
	c.Header("ETag", result.ETag)
	c.Header("Cache-Control", "private, max-age=31536000, immutable")
	c.Header("X-FSCM-Media-Cache", result.State)
	if c.GetHeader("If-None-Match") == result.ETag {
		c.Status(http.StatusNotModified)
		return
	}
	c.Data(http.StatusOK, result.ContentType, result.Body)
}

func validMediaVersion(version string) bool {
	if len(version) < 1 || len(version) > 128 {
		return false
	}
	for _, value := range version {
		if (value < 'a' || value > 'z') && (value < 'A' || value > 'Z') &&
			(value < '0' || value > '9') && value != '-' && value != '_' && value != '.' {
			return false
		}
	}
	return true
}

func serveCatalogBoxLabels(c *gin.Context, catalogManager *catalog.Manager, proxyHandler *edgeproxy.Handler, confirmCenter bool) {
	filter, ok := readBoxLabelFilter(c)
	if !ok {
		return
	}
	if !catalogRequestAuthorized(c, catalogManager, proxyHandler) {
		return
	}
	status, err := catalogManager.Status()
	if err != nil || !status.Ready || !status.BoxLabelsReady {
		proxyHandler.ServeHTTP(c.Writer, c.Request)
		return
	}
	if confirmCenter && catalogManager.ConfirmChangesIfDue(30*time.Second) {
		c.Header("X-FSCM-Catalog-Refresh", "scheduled")
	}
	items, total, err := catalogManager.SearchBoxLabels(filter)
	if err != nil {
		proxyHandler.ServeHTTP(c.Writer, c.Request)
		return
	}
	c.Header("X-FSCM-Cache", "CATALOG-HIT")
	c.Header("X-FSCM-Served-By", "edge-catalog")
	c.JSON(http.StatusOK, gin.H{"code": 0, "data": gin.H{"data": items, "total": total, "page": filter.Page, "page_size": filter.PageSize}, "msg": "ok"})
}

func serveCatalogBoxLabel(c *gin.Context, catalogManager *catalog.Manager, proxyHandler *edgeproxy.Handler, confirmCenter bool) {
	if !catalogRequestAuthorized(c, catalogManager, proxyHandler) {
		return
	}
	id, err := strconv.ParseUint(strings.TrimSpace(c.Param("id")), 10, 32)
	if err != nil || id == 0 {
		c.JSON(http.StatusBadRequest, gin.H{"code": http.StatusBadRequest, "data": nil, "msg": "invalid box label id"})
		return
	}
	status, err := catalogManager.Status()
	if err != nil || !status.Ready || !status.BoxLabelsReady {
		proxyHandler.ServeHTTP(c.Writer, c.Request)
		return
	}
	if confirmCenter && catalogManager.ConfirmChangesIfDue(30*time.Second) {
		c.Header("X-FSCM-Catalog-Refresh", "scheduled")
	}
	item, err := catalogManager.GetBoxLabel(uint(id))
	if err != nil {
		c.JSON(http.StatusNotFound, gin.H{"code": http.StatusNotFound, "data": nil, "msg": "box label not found"})
		return
	}
	c.Header("X-FSCM-Cache", "CATALOG-HIT")
	c.Header("X-FSCM-Served-By", "edge-catalog")
	c.JSON(http.StatusOK, gin.H{"code": 0, "data": item, "msg": "ok"})
}

func serveCatalogBoxLabelResolve(c *gin.Context, catalogManager *catalog.Manager, proxyHandler *edgeproxy.Handler, confirmCenter bool) {
	if !catalogRequestAuthorized(c, catalogManager, proxyHandler) {
		return
	}
	status, err := catalogManager.Status()
	if err != nil || !status.Ready || !status.BoxLabelsReady {
		proxyHandler.ServeHTTP(c.Writer, c.Request)
		return
	}
	if confirmCenter && catalogManager.ConfirmChangesIfDue(30*time.Second) {
		c.Header("X-FSCM-Catalog-Refresh", "scheduled")
	}
	item, err := catalogManager.ResolveBoxLabel(c.Param("id"))
	if err != nil {
		c.JSON(http.StatusNotFound, gin.H{"code": http.StatusNotFound, "data": nil, "msg": "箱唛不存在"})
		return
	}
	c.Header("X-FSCM-Cache", "CATALOG-HIT")
	c.Header("X-FSCM-Served-By", "edge-catalog")
	c.JSON(http.StatusOK, gin.H{"code": 0, "data": buildBoxLabelResolvePayload(item), "msg": "ok"})
}

func buildBoxLabelResolvePayload(item *catalog.BoxLabel) gin.H {
	valid := boxLabelStatusActive(item.Status) && item.Receiving.ReceivingRecord == nil
	warningCode, warningMessage := "", ""
	warnings := make([]string, 0, 2)
	if !boxLabelStatusActive(item.Status) {
		warningCode = "box_label_inactive"
		warnings = append(warnings, "箱唛已作废、短装(已取消)、缺货标记或订单调整移出")
	}
	if item.Receiving.ReceivingRecord != nil {
		if warningCode == "" {
			warningCode = "box_label_received"
		}
		warnings = append(warnings, "箱唛已生成收货记录")
	}
	warningMessage = strings.Join(warnings, "；")
	supplierOrder := gin.H{"id": item.SupplierOrder.ID, "code": item.SupplierOrder.Code, "status": item.SupplierOrder.Status}
	if item.ManufacturerID > 0 || item.ManufacturerName != "" {
		supplierOrder["manufacturer"] = gin.H{"id": item.ManufacturerID, "name": item.ManufacturerName}
	}
	if item.PurchaseOrder != nil {
		supplierOrder["purchase_order"] = item.PurchaseOrder
	}
	payload := gin.H{
		"id": item.ID, "printable": item.Printable, "print_snapshot": item.PrintSnapshot,
		"recognized": true, "valid_for_current_context": valid,
		"box_uid": item.BoxUID, "label_code": item.LabelCode, "box_no": item.BoxNo,
		"status": item.Status, "tracking_status": item.Status,
		"warning_code": warningCode, "warning_message": warningMessage, "warnings": warnings,
		"case_spec_name": item.CaseSpecName, "planned_box_qty": item.PlannedBoxQty, "sku_items": item.SKUItems,
		"warehouse_code": item.Receiving.WarehouseCode, "location_code": item.Receiving.LocationCode,
		"supplier_order": supplierOrder,
		"current_links":  gin.H{"supplier_order_id": item.SupplierOrder.ID},
	}
	if item.PurchaseOrder != nil {
		payload["purchase_order"] = item.PurchaseOrder
	}
	if item.CentralReceipt != nil {
		payload["central_receipt"] = item.CentralReceipt
		payload["current_links"].(gin.H)["central_receipt_id"] = item.CentralReceipt.ID
	}
	if item.Receiving.ScannedAt != nil {
		payload["central_received_at"] = item.Receiving.ScannedAt
	}
	if len(item.SKUItems) > 0 {
		sku := item.SKUItems[0]
		payload["sku_code"], payload["product_code"], payload["product_name"] = sku.SKUCode, sku.ProductCode, sku.ProductName
		payload["qty_per_carton"] = sku.QtyPerBox
		payload["current_links"].(gin.H)["sku_id"] = sku.SKUID
		payload["current_links"].(gin.H)["product_id"] = sku.ProductID
	}
	if item.ConsolidationOrder != nil {
		payload["consolidation_order"] = item.ConsolidationOrder
		payload["current_links"].(gin.H)["consolidation_order_id"] = item.ConsolidationOrder.ID
	}
	if item.ConsolidationContainer != "" {
		payload["consolidation_container"] = gin.H{"container_no": item.ConsolidationContainer}
	}
	if item.Receiving.Session != nil {
		payload["receiving_session"] = item.Receiving.Session
		payload["current_links"].(gin.H)["receiving_session_id"] = item.Receiving.Session.ID
	}
	if item.Receiving.ReceivingRecord != nil {
		payload["receiving_record"] = item.Receiving.ReceivingRecord
		payload["current_links"].(gin.H)["receiving_record_id"] = item.Receiving.ReceivingRecord.ID
	}
	return payload
}

func boxLabelStatusActive(status string) bool {
	switch strings.ToLower(strings.TrimSpace(status)) {
	case "cancelled", "shorted", "voided", "shortage_marked", "adjusted_out":
		return false
	default:
		return true
	}
}

func centerRecentlyReachable(proxyHandler *edgeproxy.Handler, reg *registry.Client) bool {
	if proxyHandler.CenterStatus().Reachable {
		return true
	}
	status := reg.Status()
	return status.LastError == "" && !status.LastSuccessAt.IsZero() && time.Since(status.LastSuccessAt) < 2*time.Minute
}

func isCompatibleCatalogSearch(c *gin.Context, sku bool) bool {
	if strings.TrimSpace(c.Query("keyword")) == "" || c.Query("region") != "" || c.Query("manufacturer_id") != "" {
		return false
	}
	matchMode := strings.ToLower(strings.TrimSpace(c.DefaultQuery("match_mode", "prefix")))
	if matchMode != "prefix" && (!sku || matchMode != "exact") {
		return false
	}
	if !sku && c.Query("product_id") != "" {
		return false
	}
	page := strings.TrimSpace(c.DefaultQuery("page", "1"))
	return page == "" || page == "1"
}

func catalogRequestAuthorized(c *gin.Context, catalogManager *catalog.Manager, _ *edgeproxy.Handler) bool {
	if err := catalogManager.AuthorizeTicket(c.GetHeader("X-Edge-Ticket")); err == nil {
		return true
	}
	c.AbortWithStatusJSON(http.StatusUnauthorized, gin.H{"code": "EDGE_CATALOG_TICKET_REQUIRED"})
	return false
}

func writeCatalogList(c *gin.Context, items interface{}) {
	total := 0
	switch result := items.(type) {
	case []catalog.Product:
		total = len(result)
	case []catalog.SKU:
		total = len(result)
	}
	writeCatalogListState(c, items, int64(total), "CATALOG-HIT")
}

func writeCatalogListState(c *gin.Context, items interface{}, total int64, state string) {
	c.Header("X-FSCM-Cache", state)
	c.Header("X-FSCM-Served-By", "edge-catalog")
	c.JSON(http.StatusOK, gin.H{"code": 0, "data": gin.H{"items": items, "total": total, "page": 1, "pageSize": total}, "msg": "ok"})
}

func writeCatalogPageState(c *gin.Context, items interface{}, total int64, page catalog.PageFilter, state string) {
	c.Header("X-FSCM-Cache", state)
	c.Header("X-FSCM-Served-By", "edge-catalog")
	c.JSON(http.StatusOK, gin.H{"code": 0, "data": gin.H{"items": items, "total": total, "page": page.Page, "pageSize": page.PageSize}, "msg": "ok"})
}

func writeCatalogObject(c *gin.Context, item interface{}, state string) {
	c.Header("X-FSCM-Cache", state)
	c.Header("X-FSCM-Served-By", "edge-catalog")
	c.JSON(http.StatusOK, gin.H{"code": 0, "data": item, "msg": "ok"})
}

func catalogCapabilities(status catalog.Status) []string {
	capabilities := []string{"proxy", "adaptive_cache", "local_print", "print_templates", "batch_print_v1"}
	if status.Ready {
		capabilities = append(capabilities, "catalog_cache", "catalog_media_cache")
	}
	if status.Ready && status.BoxLabelsReady {
		capabilities = append(capabilities, "box_label_catalog")
	}
	return capabilities
}

func cors() gin.HandlerFunc {
	return func(c *gin.Context) {
		c.Header("Access-Control-Allow-Origin", "*")
		c.Header("Access-Control-Allow-Methods", "GET,POST,PUT,PATCH,DELETE,HEAD,OPTIONS")
		c.Header("Access-Control-Allow-Headers", "Content-Type,Authorization,X-API-Token,X-Namespace-ID,X-Edge-Ticket,X-Edge-Admin-Token")
		if c.Request.Method == http.MethodOptions {
			c.AbortWithStatus(http.StatusNoContent)
			return
		}
		c.Next()
	}
}
func adminMiddleware(token string) gin.HandlerFunc {
	return func(c *gin.Context) {
		if token != "" && subtle.ConstantTimeCompare([]byte(c.GetHeader("X-Edge-Admin-Token")), []byte(token)) == 1 {
			c.Next()
			return
		}
		c.AbortWithStatusJSON(http.StatusUnauthorized, gin.H{"code": "EDGE_ADMIN_UNAUTHORIZED"})
	}
}
func localIPv4() string {
	type candidate struct {
		ip    net.IP
		score int
	}
	var candidates []candidate
	interfaces, _ := net.Interfaces()
	for _, iface := range interfaces {
		if iface.Flags&net.FlagUp == 0 || iface.Flags&net.FlagLoopback != 0 {
			continue
		}
		addresses, _ := iface.Addrs()
		for _, address := range addresses {
			network, ok := address.(*net.IPNet)
			if !ok {
				continue
			}
			ip := network.IP.To4()
			if ip == nil || !isPublishableLANIPv4(ip) {
				continue
			}
			score := 1
			if isPrivateIPv4(ip) {
				score = 10
			}
			candidates = append(candidates, candidate{ip: ip, score: score})
		}
	}
	if len(candidates) == 0 {
		return ""
	}
	best := candidates[0]
	for _, value := range candidates[1:] {
		if value.score > best.score || (value.score == best.score && bytesCompareIP(value.ip, best.ip) < 0) {
			best = value
		}
	}
	return best.ip.String()
}

func isPublishableLANIPv4(ip net.IP) bool {
	return ip != nil && !ip.IsLoopback() && !ip.IsUnspecified() && !ip.IsMulticast() && !ip.IsLinkLocalUnicast()
}

func isPrivateIPv4(ip net.IP) bool {
	return ip.IsPrivate()
}

func bytesCompareIP(left, right net.IP) int {
	for index := 0; index < len(left) && index < len(right); index++ {
		if left[index] < right[index] {
			return -1
		}
		if left[index] > right[index] {
			return 1
		}
	}
	return len(left) - len(right)
}
func writeTerminalCommandResponse(c *gin.Context, result terminalCommandResult, err error) {
	if err == nil {
		c.JSON(http.StatusOK, gin.H{"command_id": result.CommandID, "status": result.Status, "message": result.Message})
		return
	}
	switch {
	case errors.Is(err, errTerminalNotFound):
		c.JSON(http.StatusNotFound, gin.H{"code": "EDGE_TERMINAL_NOT_FOUND"})
	case errors.Is(err, errTerminalOffline), errors.Is(err, errFindDeviceDisabled):
		c.JSON(http.StatusConflict, gin.H{"code": "EDGE_TERMINAL_UNAVAILABLE", "message": err.Error()})
	case errors.Is(err, errTerminalTimeout):
		c.JSON(http.StatusGatewayTimeout, gin.H{"code": "EDGE_TERMINAL_COMMAND_TIMEOUT"})
	default:
		c.JSON(http.StatusBadGateway, gin.H{"code": "EDGE_TERMINAL_COMMAND_FAILED", "message": err.Error()})
	}
}
func first(values ...string) string {
	for _, value := range values {
		if strings.TrimSpace(value) != "" {
			return strings.TrimSpace(value)
		}
	}
	return ""
}
