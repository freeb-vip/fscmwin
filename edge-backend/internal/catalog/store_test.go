package catalog

import (
	"path/filepath"
	"testing"
)

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
