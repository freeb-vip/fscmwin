package main

import (
	"context"
	"crypto/subtle"
	"encoding/json"
	"flag"
	"fmt"
	"net"
	"net/http"
	"os"
	"os/signal"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"syscall"
	"time"

	"fscm-edge/internal/cache"
	"fscm-edge/internal/catalog"
	"fscm-edge/internal/config"
	"fscm-edge/internal/printing"
	edgeproxy "fscm-edge/internal/proxy"
	"fscm-edge/internal/registry"
	"fscm-edge/internal/version"

	"github.com/gin-gonic/gin"
)

type terminal struct {
	Name       string    `json:"name"`
	IP         string    `json:"ip"`
	UserAgent  string    `json:"user_agent"`
	Source     string    `json:"source"`
	LastSeenAt time.Time `json:"last_seen_at"`
	Status     string    `json:"status"`
}
type terminalStore struct {
	sync.RWMutex
	items map[string]terminal
}

type printerAvailability struct {
	sync.RWMutex
	printers map[string]struct{}
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
	a.Unlock()
}

func (a *printerAvailability) Has(printer string) bool {
	a.RLock()
	defer a.RUnlock()
	_, ok := a.printers[strings.TrimSpace(printer)]
	return ok
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
	availability := &printerAvailability{printers: make(map[string]struct{})}
	reg := registry.New(registry.Config{CenterURL: cfg.CenterURL, APIToken: cfg.APIToken, NodeID: cfg.NodeID, NodeName: cfg.NodeName, LANBaseURL: cfg.LANBaseURL, Version: version.Version, APIVersion: version.APIVersion, CacheMode: cfg.Cache.Mode, NamespaceID: cfg.NamespaceID, Capabilities: []string{"proxy", "adaptive_cache", "catalog_cache", "local_print", "print_templates"}, Inventory: func() interface{} { return printInventory(printer.Templates(), cfg.DefaultPrinter, availability) }, HeartbeatInterval: time.Duration(cfg.HeartbeatSeconds) * time.Second, OnCatalogRevision: catalogManager.OnRemoteRevision, OnTicketPublicKey: catalogManager.SetTicketPublicKey})
	ctx, cancel := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer cancel()
	reg.Start(ctx)
	catalogManager.Start(ctx)
	startRemotePrintQueue(ctx, reg, printer, time.Duration(cfg.PrintPollSeconds)*time.Second)
	startLocalPrintAuditSync(ctx, reg, printer, 5*time.Second)

	terminals := &terminalStore{items: make(map[string]terminal)}
	router := gin.Default()
	router.Use(cors())
	router.GET("/edge/health", func(c *gin.Context) {
		catalogStatus, _ := catalogManager.Status()
		c.JSON(http.StatusOK, gin.H{"status": "ok", "mode": "proxy", "backend_version": version.Version, "backend_commit": version.Commit, "edge_api_version": version.APIVersion, "center": proxyHandler.CenterStatus(), "cache": responseCache.Status(), "catalog": catalogStatus, "registration": reg.Status()})
	})
	router.GET("/edge/probe", func(c *gin.Context) {
		terminals.record(c)
		c.JSON(http.StatusOK, gin.H{"ok": true, "node_id": cfg.NodeID, "node_name": cfg.NodeName, "lan_base_url": cfg.LANBaseURL, "capabilities": []string{"proxy", "adaptive_cache", "catalog_cache", "local_print"}, "cache_mode": cfg.Cache.Mode})
	})
	router.GET("/edge/label-print", serveLabelPrintPage)
	router.GET("/edge/web/label-templates", func(c *gin.Context) {
		c.JSON(http.StatusOK, gin.H{"templates": availableLabelTemplates(printer.Templates(), availability)})
	})
	router.POST("/edge/web/label-jobs", func(c *gin.Context) {
		createWebLabelJob(c, printer, availability)
	})
	// /api/* always remains a transparent center proxy. LAN-only print uses
	// an explicit edge path so central print requests cannot be intercepted.
	router.POST("/edge/print-jobs/direct", func(c *gin.Context) {
		createCompatibleManualTextJob(c, printer, availability)
	})
	router.GET("/api/products", func(c *gin.Context) {
		serveCatalogProducts(c, catalogManager, proxyHandler)
	})
	router.GET("/api/skus", func(c *gin.Context) {
		serveCatalogSKUs(c, catalogManager, proxyHandler)
	})

	admin := router.Group("/edge", adminMiddleware(cfg.AdminToken))
	admin.GET("/capabilities", func(c *gin.Context) {
		c.JSON(http.StatusOK, gin.H{"capabilities": []string{"proxy", "adaptive_cache", "catalog_cache", "local_print"}})
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
		items, err := catalogManager.SearchProducts(c.Query("keyword"))
		if err != nil {
			c.JSON(http.StatusInternalServerError, gin.H{"code": "CATALOG_SEARCH_FAILED"})
			return
		}
		status, _ := catalogManager.Status()
		c.JSON(http.StatusOK, gin.H{"items": items, "catalog": status, "source": "local"})
	})
	admin.GET("/catalog/skus/search", func(c *gin.Context) {
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
		items, err := catalogManager.SearchSKUs(c.Query("keyword"), productID)
		if err != nil {
			c.JSON(http.StatusInternalServerError, gin.H{"code": "CATALOG_SEARCH_FAILED"})
			return
		}
		status, _ := catalogManager.Status()
		c.JSON(http.StatusOK, gin.H{"items": items, "catalog": status, "source": "local"})
	})
	admin.GET("/cache/status", func(c *gin.Context) {
		c.JSON(http.StatusOK, gin.H{"cache": responseCache.Status(), "center": proxyHandler.CenterStatus(), "whitelist": cfg.Cache.Whitelist})
	})
	admin.POST("/cache/clear", func(c *gin.Context) { c.JSON(http.StatusOK, gin.H{"cleared": responseCache.Clear()}) })
	admin.GET("/terminals", func(c *gin.Context) { c.JSON(http.StatusOK, gin.H{"terminals": terminals.list()}) })
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
		claimed, err := claimRemotePrintJob(c.Request.Context(), reg, printer)
		if err != nil {
			c.JSON(http.StatusBadGateway, gin.H{"code": "CENTER_PRINT_JOB_PULL_FAILED"})
			return
		}
		c.JSON(http.StatusOK, gin.H{"claimed": claimed})
	})
	router.POST("/edge/print-jobs", func(c *gin.Context) {
		var req printing.Request
		_ = c.ShouldBindJSON(&req)
		if req.TemplateID != "" && !printer.HasTemplate(req.TemplateID) {
			c.JSON(http.StatusBadRequest, gin.H{"code": "PRINT_TEMPLATE_NOT_FOUND"})
			return
		}
		job, duplicate, err := printer.Create(req)
		if err != nil {
			c.JSON(http.StatusInternalServerError, gin.H{"code": "PRINT_JOB_PERSIST_FAILED"})
			return
		}
		c.JSON(http.StatusAccepted, gin.H{"status": "accepted", "duplicate": duplicate, "job": job})
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
		if job.RemoteJobID > 0 && (payload.Status == "completed" || payload.Status == "failed") {
			centerStatus := "succeeded"
			if payload.Status == "failed" {
				centerStatus = "failed"
			}
			if err := reg.CompletePrintJob(c.Request.Context(), job.RemoteJobID, job.RemoteLeaseToken, centerStatus, payload.Error, map[string]interface{}{"local_job_id": job.ID, "printer": job.Printer}); err != nil {
				c.JSON(http.StatusBadGateway, gin.H{"code": "CENTER_PRINT_JOB_COMPLETE_FAILED", "job": job})
				return
			}
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
	result := make([]map[string]interface{}, 0, len(templates))
	for _, template := range templates {
		if !availability.Has(template.Printer) {
			continue
		}
		addPrinter(template.Printer)
		result = append(result, map[string]interface{}{
			"code": template.ID, "name": template.Name, "type": inventoryTemplateType(template), "printer_name": template.Printer,
			"width_mm": template.WidthMillimeters, "height_mm": template.HeightMillimeters, "orientation": template.Orientation,
			"version": "1", "available": true,
		})
	}
	return map[string]interface{}{"printers": printers, "templates": result}
}

func inventoryTemplateType(template printing.Template) string {
	// Template purpose is an explicit user setting. Paper size does not define it:
	// a 100 x 150 mm template may be a shipping label, a generic label, or a box mark.
	return first(strings.ToLower(strings.TrimSpace(template.Type)), "label")
}

func startRemotePrintQueue(ctx context.Context, reg *registry.Client, printer *printing.Service, interval time.Duration) {
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
				_, _ = claimRemotePrintJob(ctx, reg, printer)
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

func syncLocalPrintAudits(ctx context.Context, reg *registry.Client, printer *printing.Service) error {
	jobs, err := printer.PendingAudits(50)
	if err != nil {
		return err
	}
	for _, job := range jobs {
		audit := registry.LocalPrintAudit{
			LocalJobID: job.ID, Source: job.Source, TemplateCode: job.TemplateID, PrinterName: job.Printer,
			JobType: first(job.Kind, "local_print"), Status: job.Status, Copies: job.Copies,
			ContentSnapshot: map[string]interface{}{"text": job.Text, "qr_code_content": job.QRCodeContent, "items": job.Items, "box_marks": job.BoxMarks},
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

func claimRemotePrintJob(ctx context.Context, reg *registry.Client, printer *printing.Service) (bool, error) {
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
		if completeErr := reg.CompletePrintJob(ctx, claim.ID, claim.LeaseToken, "failed", "invalid print job payload", nil); completeErr != nil {
			return false, completeErr
		}
		return true, nil
	}
	localID := fmt.Sprintf("center-job-%d", claim.ID)
	request := printing.Request{JobID: localID, IdempotencyKey: localID, Source: "center", Printer: claim.PrinterName, Template: claim.TemplateCode, TemplateID: claim.TemplateCode, Kind: payload.Kind, Text: payload.Text, QRCodeContent: payload.Text, Copies: claim.Copies, BoxMarks: payload.Items}
	if payload.Kind == "manual_text" {
		// Keep one item for older Windows workers that predate the manual_text
		// dispatcher. New workers render Text directly; older workers print this QR item.
		request.Items = []printing.Item{{SKUCode: payload.Text, QRCodeContent: payload.Text, Quantity: claim.Copies}}
	} else if payload.Kind != "manufacturer_box_mark" {
		request.Items = []printing.Item{{SKUID: payload.SKUID, SKUCode: payload.SKUCode, QRCodeContent: payload.QRPayload, Quantity: claim.Copies}}
	}
	if _, _, err := printer.CreateRemote(request, claim.ID, claim.LeaseToken); err != nil {
		return false, err
	}
	return true, nil
}

func validClaimPayload(kind, skuCode, text string, items []printing.BoxMark) bool {
	switch kind {
	case "manual_text":
		return strings.TrimSpace(text) != ""
	case "manufacturer_box_mark":
		return len(items) > 0
	default:
		return strings.TrimSpace(skuCode) != ""
	}
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
	if !isCompatibleCatalogSearch(c, false) {
		proxyHandler.ServeHTTP(c.Writer, c.Request)
		return
	}
	if !catalogRequestAuthorized(c, catalogManager, proxyHandler) {
		return
	}
	status, err := catalogManager.Status()
	if err != nil || !status.Ready {
		proxyHandler.ServeHTTP(c.Writer, c.Request)
		return
	}
	items, err := catalogManager.SearchProducts(c.Query("keyword"))
	if err != nil {
		proxyHandler.ServeHTTP(c.Writer, c.Request)
		return
	}
	writeCatalogList(c, items)
}

func serveCatalogSKUs(c *gin.Context, catalogManager *catalog.Manager, proxyHandler *edgeproxy.Handler) {
	if !isCompatibleCatalogSearch(c, true) {
		proxyHandler.ServeHTTP(c.Writer, c.Request)
		return
	}
	if !catalogRequestAuthorized(c, catalogManager, proxyHandler) {
		return
	}
	status, err := catalogManager.Status()
	if err != nil || !status.Ready {
		proxyHandler.ServeHTTP(c.Writer, c.Request)
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
	items, err := catalogManager.SearchSKUs(c.Query("keyword"), productID)
	if err != nil {
		proxyHandler.ServeHTTP(c.Writer, c.Request)
		return
	}
	writeCatalogList(c, items)
}

func isCompatibleCatalogSearch(c *gin.Context, sku bool) bool {
	if strings.TrimSpace(c.Query("keyword")) == "" || c.Query("region") != "" || c.Query("manufacturer_id") != "" {
		return false
	}
	matchMode := strings.ToLower(strings.TrimSpace(c.DefaultQuery("match_mode", "prefix")))
	if matchMode != "prefix" {
		return false
	}
	if !sku && c.Query("product_id") != "" {
		return false
	}
	page := strings.TrimSpace(c.DefaultQuery("page", "1"))
	return page == "" || page == "1"
}

func catalogRequestAuthorized(c *gin.Context, catalogManager *catalog.Manager, proxyHandler *edgeproxy.Handler) bool {
	if err := catalogManager.AuthorizeTicket(c.GetHeader("X-Edge-Ticket")); err == nil {
		return true
	}
	if proxyHandler.CenterStatus().Reachable {
		proxyHandler.ServeHTTP(c.Writer, c.Request)
		return false
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
	c.Header("X-FSCM-Cache", "CATALOG-HIT")
	c.Header("X-FSCM-Served-By", "edge-catalog")
	c.JSON(http.StatusOK, gin.H{"code": 0, "data": gin.H{"items": items, "total": total, "page": 1, "pageSize": total}, "msg": "ok"})
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
func (s *terminalStore) record(c *gin.Context) {
	ip := c.ClientIP()
	value := terminal{Name: first(c.GetHeader("X-Edge-Terminal-Name"), ip), IP: ip, UserAgent: c.Request.UserAgent(), Source: "probe", LastSeenAt: time.Now(), Status: "online"}
	s.Lock()
	s.items[ip+"|"+value.UserAgent] = value
	s.Unlock()
}
func (s *terminalStore) list() []terminal {
	s.RLock()
	defer s.RUnlock()
	result := make([]terminal, 0, len(s.items))
	for _, value := range s.items {
		result = append(result, value)
	}
	return result
}
func first(values ...string) string {
	for _, value := range values {
		if strings.TrimSpace(value) != "" {
			return strings.TrimSpace(value)
		}
	}
	return ""
}
