package main

import (
	"testing"

	"fscm-edge/internal/printing"
)

func TestValidateManufacturerBoxMarkRequest(t *testing.T) {
	availability := &printerAvailability{printers: make(map[string]struct{})}
	availability.Set([]string{"Label Printer"})
	template := printing.Template{ID: "box", Type: "manufacturer_box_mark", Printer: "Label Printer", WidthMillimeters: 100, HeightMillimeters: 150, Orientation: "portrait"}
	request := printing.Request{Kind: "manufacturer_box_mark", TemplateID: "box", Copies: 1, BoxMarks: []printing.BoxMark{{BoxPlanID: 1}}}
	if code := validateManufacturerBoxMarkRequest(request, []printing.Template{template}, availability); code != "" {
		t.Fatalf("valid request rejected with %s", code)
	}
	request.BoxMarks = make([]printing.BoxMark, 101)
	if code := validateManufacturerBoxMarkRequest(request, []printing.Template{template}, availability); code != "INVALID_BOX_MARK_COUNT" {
		t.Fatalf("oversized request returned %s", code)
	}
	request.BoxMarks = []printing.BoxMark{{BoxPlanID: 1}}
	template.WidthMillimeters = 99
	if code := validateManufacturerBoxMarkRequest(request, []printing.Template{template}, availability); code != "INVALID_BOX_MARK_TEMPLATE" {
		t.Fatalf("invalid template returned %s", code)
	}
	availability.Set(nil)
	template.WidthMillimeters = 100
	if code := validateManufacturerBoxMarkRequest(request, []printing.Template{template}, availability); code != "BOX_MARK_PRINTER_UNAVAILABLE" {
		t.Fatalf("offline printer returned %s", code)
	}
}
