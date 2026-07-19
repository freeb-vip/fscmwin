package catalog

import (
	"database/sql"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"path/filepath"
	"testing"
	"time"
)

func TestStoreMigratesLegacyCatalogAndRequiresBoxLabelInitialization(t *testing.T) {
	path := filepath.Join(t.TempDir(), "legacy.db")
	db, err := sql.Open("sqlite", path)
	if err != nil {
		t.Fatal(err)
	}
	_, err = db.Exec(`CREATE TABLE catalog_meta (namespace_id INTEGER PRIMARY KEY, active_generation INTEGER NOT NULL DEFAULT 0, revision INTEGER NOT NULL DEFAULT 0, last_full_sync_at TEXT NOT NULL DEFAULT '', state TEXT NOT NULL DEFAULT 'empty', last_error TEXT NOT NULL DEFAULT ''); INSERT INTO catalog_meta(namespace_id,active_generation,state) VALUES(7,1,'ready')`)
	if err != nil {
		t.Fatal(err)
	}
	if err = db.Close(); err != nil {
		t.Fatal(err)
	}
	store, err := Open(path)
	if err != nil {
		t.Fatal(err)
	}
	defer store.Close()
	status, err := store.Status(7)
	if err != nil {
		t.Fatal(err)
	}
	if !status.Ready || status.BoxLabelsReady {
		t.Fatalf("legacy status ready=%v box_labels_ready=%v", status.Ready, status.BoxLabelsReady)
	}
}

func TestStoreSearchesQRCodePrefixAndPersists(t *testing.T) {
	path := filepath.Join(t.TempDir(), "edge.db")
	store, err := Open(path)
	if err != nil {
		t.Fatal(err)
	}
	generation, err := store.BeginFullSync(7)
	if err != nil {
		t.Fatal(err)
	}
	if err := store.UpsertSKUs(7, generation, []SKU{
		{ID: 1, Code: "B10-RED", ProductID: 10, ProductCode: "B10"},
		{ID: 2, Code: "B10-BLUE", ProductID: 10, ProductCode: "B10"},
		{ID: 3, Code: "TB10-SPECIAL", ProductID: 11, ProductCode: "TB10"},
	}); err != nil {
		t.Fatal(err)
	}
	if err := store.FinishFullSync(7, generation, 9); err != nil {
		t.Fatal(err)
	}
	items, err := store.SearchSKUs(7, "TB10-", "T", nil)
	if err != nil {
		t.Fatal(err)
	}
	if len(items) != 3 {
		t.Fatalf("prefix search returned %d items, want 3", len(items))
	}
	if err := store.Close(); err != nil {
		t.Fatal(err)
	}

	store, err = Open(path)
	if err != nil {
		t.Fatal(err)
	}
	defer store.Close()
	items, err = store.SearchSKUs(7, "B10-", "T", nil)
	if err != nil {
		t.Fatal(err)
	}
	if len(items) != 2 {
		t.Fatalf("persisted prefix search returned %d items, want 2", len(items))
	}
}

func TestStorePaginatesAndCachesProductsAndSKUs(t *testing.T) {
	path := filepath.Join(t.TempDir(), "edge.db")
	store, err := Open(path)
	if err != nil {
		t.Fatal(err)
	}
	defer store.Close()
	generation, err := store.BeginFullSync(7)
	if err != nil {
		t.Fatal(err)
	}
	if err = store.UpsertProducts(7, generation, []Product{{ID: 1, Code: "P-001"}, {ID: 2, Code: "P-002"}, {ID: 3, Code: "P-003"}}); err != nil {
		t.Fatal(err)
	}
	media := &MediaRef{ID: "sku:12", Version: "v1", ThumbnailPath: "/edge/v2/catalog/media/sku/12/thumbnail?v=v1", CentralURL: "https://center/sku-12.jpg"}
	if err = store.UpsertSKUs(7, generation, []SKU{{ID: 11, Code: "SKU-001", ProductID: 1}, {ID: 12, Code: "SKU-002", ProductID: 1, Media: media}, {ID: 13, Code: "SKU-003", ProductID: 2}}); err != nil {
		t.Fatal(err)
	}
	if err = store.FinishFullSync(7, generation, 1); err != nil {
		t.Fatal(err)
	}

	products, total, err := store.SearchProductsPage(7, "P-", PageFilter{Page: 2, PageSize: 2})
	if err != nil || total != 3 || len(products) != 1 || products[0].ID != 3 {
		t.Fatalf("product page total=%d items=%+v err=%v", total, products, err)
	}
	productID := uint(1)
	skus, total, err := store.SearchSKUsPage(7, "SKU-", "T", &productID, PageFilter{Page: 1, PageSize: 1})
	if err != nil || total != 2 || len(skus) != 1 || skus[0].ID != 11 {
		t.Fatalf("sku page total=%d items=%+v err=%v", total, skus, err)
	}
	exact, total, err := store.SearchSKUsPageMode(7, "sku-002", "T", nil, "exact", PageFilter{Page: 1, PageSize: 20})
	if err != nil || total != 1 || len(exact) != 1 || exact[0].ID != 12 {
		t.Fatalf("exact sku total=%d items=%+v err=%v", total, exact, err)
	}
	detail, err := store.GetSKU(7, "12")
	if err != nil || detail == nil || detail.Code != "SKU-002" || detail.Media == nil || detail.Media.Version != "v1" || detail.Media.CentralURL != media.CentralURL {
		t.Fatalf("sku detail=%+v err=%v", detail, err)
	}

	if err = store.CacheProducts(7, []Product{{ID: 4, Code: "P-004"}}); err != nil {
		t.Fatal(err)
	}
	if err = store.CacheSKUs(7, []SKU{{ID: 14, Code: "SKU-004", ProductID: 4}}); err != nil {
		t.Fatal(err)
	}
	products, total, err = store.SearchProductsPage(7, "P-004", PageFilter{Page: 1, PageSize: 20})
	if err != nil || total != 1 || len(products) != 1 || products[0].ID != 4 {
		t.Fatalf("cached product total=%d items=%+v err=%v", total, products, err)
	}
	skus, total, err = store.SearchSKUsPage(7, "SKU-004", "T", nil, PageFilter{Page: 1, PageSize: 20})
	if err != nil || total != 1 || len(skus) != 1 || skus[0].ID != 14 {
		t.Fatalf("cached sku total=%d items=%+v err=%v", total, skus, err)
	}
}

func TestStoreSearchesBoxLabelsWithCombinedProductAndConsolidationFilters(t *testing.T) {
	store, err := Open(filepath.Join(t.TempDir(), "edge.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer store.Close()
	generation, err := store.BeginFullSync(7)
	if err != nil {
		t.Fatal(err)
	}
	created := time.Date(2026, 7, 16, 12, 0, 0, 0, time.UTC)
	labels := []BoxLabel{
		{ID: 10, LabelCode: "BX-10", StatusGroup: "normal", SupplierOrder: DocumentRef{Code: "SO-1"}, ConsolidationOrder: &DocumentRef{ID: 8, Code: "CON-8"}, Receiving: BoxLabelReceiving{Status: "pending"}, CreatedAt: created, Printable: true, SKUItems: []BoxLabelSKUItem{{SKUID: 101, SKUCode: "SKU-A", ProductID: 201}, {SKUID: 102, SKUCode: "SKU-B", ProductID: 202}}, PrintSnapshot: &BoxMark{BoxPlanID: 10, BoxQRPayload: "BX-10"}},
		{ID: 11, LabelCode: "BX-11", StatusGroup: "normal", SupplierOrder: DocumentRef{Code: "SO-2"}, ConsolidationOrder: &DocumentRef{ID: 9, Code: "CON-9"}, Receiving: BoxLabelReceiving{Status: "received"}, CreatedAt: created.Add(time.Minute), SKUItems: []BoxLabelSKUItem{{SKUID: 103, SKUCode: "SKU-C", ProductID: 201}}},
	}
	if err = store.UpsertBoxLabels(7, generation, labels); err != nil {
		t.Fatal(err)
	}
	if err = store.FinishFullSync(7, generation, 4); err != nil {
		t.Fatal(err)
	}
	items, total, err := store.SearchBoxLabels(7, BoxLabelFilter{ProductID: 201, ConsolidationOrderCode: "con-8", ReceivingStatus: "pending", Page: 1, PageSize: 20})
	if err != nil {
		t.Fatal(err)
	}
	if total != 1 || len(items) != 1 || items[0].ID != 10 || items[0].PrintSnapshot == nil {
		t.Fatalf("combined filter returned total=%d items=%+v", total, items)
	}
	items, total, err = store.SearchBoxLabels(7, BoxLabelFilter{SKUID: 102, Keyword: "SKU-B", Page: 1, PageSize: 20})
	if err != nil || total != 1 || items[0].ID != 10 {
		t.Fatalf("mixed SKU filter returned total=%d items=%+v err=%v", total, items, err)
	}
	if err = store.CacheBoxLabels(7, []BoxLabel{{ID: 12, LabelCode: "BX-CENTER", SupplierOrder: DocumentRef{Code: "SO-CENTER"}, Receiving: BoxLabelReceiving{Status: "pending"}, CreatedAt: created.Add(2 * time.Minute)}}); err != nil {
		t.Fatal(err)
	}
	items, total, err = store.SearchBoxLabels(7, BoxLabelFilter{Keyword: "BX-CENTER", Page: 1, PageSize: 20})
	if err != nil || total != 1 || items[0].ID != 12 {
		t.Fatalf("center cache returned total=%d items=%+v err=%v", total, items, err)
	}
}

func TestStoreResolvesBoxLabelCodesAndQRPayloads(t *testing.T) {
	store, err := Open(filepath.Join(t.TempDir(), "edge.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer store.Close()
	generation, err := store.BeginFullSync(5)
	if err != nil {
		t.Fatal(err)
	}
	label := BoxLabel{ID: 50, LabelCode: "AB12CD34EF56", BoxUID: "BOX-UID-50", BoxNo: "50", CreatedAt: time.Now()}
	if err = store.UpsertBoxLabels(5, generation, []BoxLabel{label}); err != nil {
		t.Fatal(err)
	}
	if err = store.FinishFullSync(5, generation, 1); err != nil {
		t.Fatal(err)
	}
	for _, raw := range []string{"AB12CD34EF56", "FSCMBOX:v1:BOX-UID-50", "FSCMBOX:v2|u=BOX-UID-50", "FSCMBOX:v3|c=AB12CD34EF56|s=SKU-A%3D2", "AB12CD34EF56,SKU-A=2"} {
		resolved, resolveErr := store.ResolveBoxLabel(5, raw)
		if resolveErr != nil || resolved.ID != 50 {
			t.Fatalf("resolve %q item=%+v err=%v", raw, resolved, resolveErr)
		}
	}
}

func TestStoreAppliesBoxLabelUpsertAndDeleteChanges(t *testing.T) {
	store, err := Open(filepath.Join(t.TempDir(), "edge.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer store.Close()
	generation, err := store.BeginFullSync(1)
	if err != nil {
		t.Fatal(err)
	}
	if err = store.FinishFullSync(1, generation, 1); err != nil {
		t.Fatal(err)
	}
	label := BoxLabel{ID: 20, LabelCode: "BX-20", SupplierOrder: DocumentRef{Code: "SO-20"}, Receiving: BoxLabelReceiving{Status: "pending"}, CreatedAt: time.Now(), SKUItems: []BoxLabelSKUItem{{SKUID: 2, ProductID: 3}}}
	payload, err := json.Marshal(label)
	if err != nil {
		t.Fatal(err)
	}
	if err = store.ApplyChanges(1, []Change{{Revision: 2, EntityType: "box_label", Action: "upsert", EntityID: 20, Payload: payload}}, 2); err != nil {
		t.Fatal(err)
	}
	if item, getErr := store.GetBoxLabel(1, 20); getErr != nil || item.LabelCode != "BX-20" {
		t.Fatalf("upsert item=%+v err=%v", item, getErr)
	}
	if err = store.ApplyChanges(1, []Change{{Revision: 3, EntityType: "box_label", Action: "delete", EntityID: 20}}, 3); err != nil {
		t.Fatal(err)
	}
	if _, getErr := store.GetBoxLabel(1, 20); getErr == nil {
		t.Fatal("deleted box label remains available")
	}
}

func TestManagerConfirmationIsBackgroundAndDeduplicated(t *testing.T) {
	requests := make(chan struct{}, 1)
	center := httptest.NewServer(http.HandlerFunc(func(writer http.ResponseWriter, request *http.Request) {
		requests <- struct{}{}
		writer.Header().Set("Content-Type", "application/json")
		_, _ = writer.Write([]byte(`{"code":0,"data":{"items":[],"next_revision":1,"catalog_revision":1,"full_sync_required":false},"msg":"ok"}`))
	}))
	defer center.Close()

	store, err := Open(filepath.Join(t.TempDir(), "edge.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer store.Close()
	generation, err := store.BeginFullSync(1)
	if err != nil {
		t.Fatal(err)
	}
	if err = store.FinishFullSync(1, generation, 1); err != nil {
		t.Fatal(err)
	}
	manager := NewManager(Config{CenterURL: center.URL, APIToken: "token", NodeID: "edge-1", NamespaceID: 1}, store)
	if !manager.ConfirmChangesIfDue(time.Hour) {
		t.Fatal("first center confirmation was not scheduled")
	}
	if manager.ConfirmChangesIfDue(time.Hour) {
		t.Fatal("duplicate center confirmation was scheduled")
	}
	select {
	case <-requests:
	case <-time.After(3 * time.Second):
		t.Fatal("center confirmation request was not sent")
	}
}

func TestStoreAppliesDeleteChange(t *testing.T) {
	store, err := Open(filepath.Join(t.TempDir(), "edge.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer store.Close()
	generation, err := store.BeginFullSync(1)
	if err != nil {
		t.Fatal(err)
	}
	if err := store.UpsertProducts(1, generation, []Product{{ID: 11, Code: "B10"}}); err != nil {
		t.Fatal(err)
	}
	if err := store.UpsertSKUs(1, generation, []SKU{{ID: 12, Code: "B10-A", ProductID: 11}}); err != nil {
		t.Fatal(err)
	}
	if err := store.FinishFullSync(1, generation, 1); err != nil {
		t.Fatal(err)
	}
	if err := store.ApplyChanges(1, []Change{{Revision: 2, EntityType: "product", Action: "delete", EntityID: 11}}, 2); err != nil {
		t.Fatal(err)
	}
	items, err := store.SearchSKUs(1, "B10", "T", nil)
	if err != nil {
		t.Fatal(err)
	}
	if len(items) != 0 {
		t.Fatalf("delete left %d SKU items", len(items))
	}
}
