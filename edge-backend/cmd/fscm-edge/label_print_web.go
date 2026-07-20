package main

import (
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"sort"
	"strings"

	"fscm-edge/internal/printing"
	"fscm-edge/internal/registry"

	"github.com/gin-gonic/gin"
)

const labelPrintPage = `<!doctype html>
<html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>FSCM 标签打印</title><style>
:root{font-family:"Segoe UI","Microsoft YaHei",sans-serif;color:#172033;background:#eef3f1}*{box-sizing:border-box}body{margin:0;min-height:100vh;display:grid;place-items:center;padding:20px}.shell{width:min(620px,100%);background:#fff;border:1px solid #d8e1df;border-radius:8px;box-shadow:0 20px 45px #19312a1a;padding:30px}h1{font-size:24px;margin:0 0 6px}.sub{color:#65716e;margin:0 0 25px}label{display:block;font-size:13px;font-weight:600;margin:17px 0 7px}input,select{width:100%;height:42px;border:1px solid #cbd7d4;border-radius:5px;background:#fff;padding:0 12px;font:inherit;color:inherit}input:focus,select:focus{outline:2px solid #2a8d78;outline-offset:1px}.row{display:grid;grid-template-columns:1fr 110px;gap:14px}.hint{font-size:12px;color:#71807c;margin-top:6px}button{margin-top:24px;width:100%;height:44px;border:0;border-radius:5px;background:#137a65;color:#fff;font:600 15px inherit;cursor:pointer}button:disabled{background:#9aaba6;cursor:wait}#status{min-height:22px;margin:16px 0 0;font-size:14px}#status.error{color:#b42318}#status.success{color:#087443}@media(max-width:520px){body{display:block;padding:12px}.shell{padding:20px;margin:0 auto}.row{grid-template-columns:1fr}h1{font-size:22px}input,select,button{height:46px}}</style></head>
<body><main class="shell"><h1>标签打印</h1><p class="sub">选择局域网边缘节点上的标签模板，提交后由本机打印机执行。</p>
<label for="template">打印模板</label><select id="template" disabled><option>正在加载可用模板...</option></select>
<label for="text">标签内容</label><input id="text" maxlength="2000" autocomplete="off" placeholder="输入任意字符串">
<div class="row"><div><label for="prefix">二维码前缀</label><input id="prefix" maxlength="20" autocomplete="off" placeholder="留空表示不添加前缀"><div class="hint">二维码使用“前缀 + 标签内容”，下方文字保持原始内容。</div></div><div><label for="copies">份数</label><input id="copies" type="number" min="1" max="100" value="1"></div></div>
<button id="submit" type="button" disabled>提交打印任务</button><div id="status" role="status"></div></main>
<script>
const template=document.querySelector('#template'),prefix=document.querySelector('#prefix'),text=document.querySelector('#text'),copies=document.querySelector('#copies'),button=document.querySelector('#submit'),status=document.querySelector('#status');
function message(value,kind=''){status.textContent=value;status.className=kind}
async function load(){try{const r=await fetch('/edge/web/label-templates');const body=await r.json();const items=body.templates||[];template.innerHTML='';if(!items.length){template.innerHTML='<option>没有可用标签模板</option>';message('请在 Windows 应用中为标签模板配置在线打印机。','error');return}for(const item of items){const o=document.createElement('option');o.value=item.Id;o.textContent=item.Name+' · '+item.WidthMillimeters+' x '+item.HeightMillimeters+' mm';o.dataset.prefix=item.LabelQrPrefix||'';template.append(o)}template.disabled=false;button.disabled=false;prefix.value=template.selectedOptions[0].dataset.prefix||''}catch{template.innerHTML='<option>无法读取标签模板</option>';message('无法连接边缘服务。','error')}}
template.addEventListener('change',()=>{prefix.value=template.selectedOptions[0].dataset.prefix||''});
button.addEventListener('click',async()=>{const value=text.value.trim();if(!value){message('请输入标签内容。','error');text.focus();return}button.disabled=true;message('正在提交打印任务...');try{const r=await fetch('/edge/web/label-jobs',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({template_id:template.value,text:value,prefix:prefix.value.trim(),copies:Number(copies.value)})});const body=await r.json();if(!r.ok)throw new Error(body.message||body.code||'提交失败');message('任务已进入打印队列：'+body.job.id,'success');text.value='';text.focus()}catch(e){message(e.message||'提交失败，请稍后重试。','error')}finally{button.disabled=false}});load();
</script></body></html>`

type webLabelPrintRequest struct {
	TemplateID   string `json:"template_id"`
	TemplateCode string `json:"template_code"`
	Text         string `json:"text"`
	LabelContent string `json:"label_content"`
	Prefix       string `json:"prefix"`
	LabelPrefix  string `json:"label_prefix"`
	Copies       int    `json:"copies"`
}

func serveLabelPrintPage(c *gin.Context) {
	c.Data(http.StatusOK, "text/html; charset=utf-8", []byte(labelPrintPage))
}

func availableLabelTemplates(templates []printing.Template, availability *printerAvailability) []printing.Template {
	available := make([]printing.Template, 0, len(templates))
	for _, template := range templates {
		if strings.EqualFold(strings.TrimSpace(template.Type), "label") &&
			strings.TrimSpace(template.Printer) != "" && availability.Has(template.Printer) {
			available = append(available, template)
		}
	}
	sort.SliceStable(available, func(left, right int) bool {
		return labelTemplatePriority(available[left]) < labelTemplatePriority(available[right])
	})
	return available
}

func createWebLabelJob(c *gin.Context, service *printing.Service, availability *printerAvailability) {
	createManualTextJob(c, service, availability)
}

type directPrintRequest struct {
	Source          string          `json:"source"`
	TemplateCode    string          `json:"template_code"`
	Copies          int             `json:"copies"`
	Type            string          `json:"type"`
	IdempotencyKey  string          `json:"idempotency_key"`
	PayloadSnapshot json.RawMessage `json:"payload_snapshot"`
}

type directSKULabelPayload struct {
	Type            string `json:"type"`
	SKUID           uint   `json:"sku_id"`
	SKUCode         string `json:"sku_code"`
	QRPayload       string `json:"qr_payload"`
	TemplateVersion string `json:"template_version"`
}

type directCustomLabelPayload struct {
	Type            string `json:"type"`
	LabelContent    string `json:"label_content"`
	Text            string `json:"text"`
	QRPayload       string `json:"qr_payload"`
	TemplateVersion string `json:"template_version"`
}

type directBoxMarkPayload struct {
	Kind            string             `json:"kind"`
	Type            string             `json:"type"`
	DocumentVersion string             `json:"document_version"`
	Items           []printing.BoxMark `json:"items"`
}

type directPrintAuthorizer func(context.Context, string) error

func createDirectPrintJob(c *gin.Context, service *printing.Service, availability *printerAvailability, authorize directPrintAuthorizer) {
	authorization := strings.TrimSpace(c.GetHeader("Authorization"))
	if authorization == "" {
		writeDirectPrintError(c, http.StatusUnauthorized, "DIRECT_PRINT_UNAUTHORIZED", "Authorization is required.")
		return
	}
	if authorize == nil {
		writeDirectPrintError(c, http.StatusServiceUnavailable, "DIRECT_PRINT_AUTH_UNAVAILABLE", "Direct print authorization is unavailable.")
		return
	}
	if err := authorize(c.Request.Context(), authorization); err != nil {
		if errors.Is(err, registry.ErrMobilePrintUnauthorized) || errors.Is(err, registry.ErrMobilePrintNodeMissing) {
			writeDirectPrintError(c, http.StatusForbidden, "DIRECT_PRINT_FORBIDDEN", "The mobile user cannot print through this edge node.")
		} else {
			writeDirectPrintError(c, http.StatusServiceUnavailable, "DIRECT_PRINT_AUTH_UNAVAILABLE", "The center service could not authorize direct printing.")
		}
		return
	}
	var request directPrintRequest
	if err := c.ShouldBindJSON(&request); err != nil {
		writeDirectPrintError(c, http.StatusBadRequest, "INVALID_DIRECT_PRINT_REQUEST", "Invalid direct print request.")
		return
	}

	request.TemplateCode = strings.TrimSpace(request.TemplateCode)
	request.IdempotencyKey = strings.TrimSpace(request.IdempotencyKey)
	if request.TemplateCode == "" {
		writeDirectPrintError(c, http.StatusBadRequest, "LABEL_TEMPLATE_REQUIRED", "template_code is required.")
		return
	}
	if request.IdempotencyKey == "" {
		writeDirectPrintError(c, http.StatusBadRequest, "IDEMPOTENCY_KEY_REQUIRED", "idempotency_key is required.")
		return
	}
	if request.Copies < 1 || request.Copies > 100 {
		writeDirectPrintError(c, http.StatusBadRequest, "INVALID_LABEL_COPIES", "copies must be between 1 and 100.")
		return
	}
	if len(request.PayloadSnapshot) == 0 || string(request.PayloadSnapshot) == "null" {
		writeDirectPrintError(c, http.StatusBadRequest, "PRINT_PAYLOAD_REQUIRED", "payload_snapshot is required.")
		return
	}

	var envelope struct {
		Type            string `json:"type"`
		Kind            string `json:"kind"`
		TemplateVersion string `json:"template_version"`
	}
	if err := json.Unmarshal(request.PayloadSnapshot, &envelope); err != nil {
		writeDirectPrintError(c, http.StatusBadRequest, "INVALID_PRINT_PAYLOAD", "payload_snapshot must be valid JSON.")
		return
	}
	jobType := strings.ToLower(firstNonEmpty(request.Type, envelope.Type, envelope.Kind))
	var selected *printing.Template
	for _, template := range service.Templates() {
		if template.ID != request.TemplateCode || strings.TrimSpace(template.Printer) == "" || !availability.Has(template.Printer) {
			continue
		}
		if jobType == "manufacturer_box_mark" {
			if strings.EqualFold(strings.TrimSpace(template.Type), "manufacturer_box_mark") {
				selected = &template
				break
			}
		} else if strings.EqualFold(strings.TrimSpace(template.Type), "label") {
			selected = &template
			break
		}
	}
	if selected == nil {
		writeDirectPrintError(c, http.StatusConflict, "LABEL_TEMPLATE_UNAVAILABLE", "The selected print template or printer is unavailable.")
		return
	}
	if strings.TrimSpace(envelope.TemplateVersion) != "" && envelope.TemplateVersion != templateVersion(*selected) {
		writeDirectPrintError(c, http.StatusConflict, "LABEL_TEMPLATE_CHANGED", "The selected print template has changed.")
		return
	}
	printRequest := printing.Request{
		IdempotencyKey:  request.IdempotencyKey,
		Source:          firstNonEmpty(request.Source, "mobile-app"),
		TemplateID:      selected.ID,
		Copies:          request.Copies,
		PayloadSnapshot: append(json.RawMessage(nil), request.PayloadSnapshot...),
	}

	switch jobType {
	case "sku_label", "sku_qr":
		var payload directSKULabelPayload
		if err := json.Unmarshal(request.PayloadSnapshot, &payload); err != nil || strings.TrimSpace(payload.SKUCode) == "" || strings.TrimSpace(payload.QRPayload) == "" {
			writeDirectPrintError(c, http.StatusBadRequest, "INVALID_SKU_LABEL_PAYLOAD", "sku_code and qr_payload are required.")
			return
		}
		printRequest.Kind = "sku_qr"
		printRequest.Items = []printing.Item{{
			SKUID: payload.SKUID, SKUCode: strings.TrimSpace(payload.SKUCode),
			QRCodeContent: strings.TrimSpace(payload.QRPayload), Quantity: request.Copies,
		}}
	case "custom_label":
		var payload directCustomLabelPayload
		if err := json.Unmarshal(request.PayloadSnapshot, &payload); err != nil {
			writeDirectPrintError(c, http.StatusBadRequest, "INVALID_CUSTOM_LABEL_PAYLOAD", "Invalid custom label payload.")
			return
		}
		text := strings.TrimSpace(firstNonEmpty(payload.LabelContent, payload.Text))
		qrPayload := strings.TrimSpace(firstNonEmpty(payload.QRPayload, text))
		if text == "" || len([]rune(text)) > 2000 || qrPayload == "" {
			writeDirectPrintError(c, http.StatusBadRequest, "INVALID_CUSTOM_LABEL_PAYLOAD", "label_content and qr_payload are required.")
			return
		}
		printRequest.Kind = "custom_label"
		printRequest.Text = text
		printRequest.QRCodeContent = qrPayload
		printRequest.Items = []printing.Item{{SKUCode: text, QRCodeContent: qrPayload, Quantity: request.Copies}}
	case "manufacturer_box_mark":
		var payload directBoxMarkPayload
		if err := json.Unmarshal(request.PayloadSnapshot, &payload); err != nil || payload.DocumentVersion != "manufacturer_box_mark.v1" || len(payload.Items) == 0 {
			writeDirectPrintError(c, http.StatusBadRequest, "INVALID_BOX_MARK_PAYLOAD", "manufacturer_box_mark.v1 and at least one item are required.")
			return
		}
		printRequest.Kind = "manufacturer_box_mark"
		printRequest.BoxMarks = payload.Items
		if code := validateManufacturerBoxMarkRequest(printRequest, service.Templates(), availability); code != "" {
			writeDirectPrintError(c, http.StatusConflict, code, "The box mark template or printer is unavailable.")
			return
		}
	default:
		writeDirectPrintError(c, http.StatusBadRequest, "UNSUPPORTED_PRINT_JOB_TYPE", "Unsupported direct print job type.")
		return
	}
	if _, message := validateLabelDisplayText(printRequest, *selected); message != "" {
		writeDirectPrintError(c, http.StatusBadRequest, "LABEL_TEXT_TOO_LONG", message)
		return
	}

	job, duplicate, err := service.Create(printRequest)
	if err != nil {
		if errors.Is(err, printing.ErrIdempotencyConflict) {
			writeDirectPrintError(c, http.StatusConflict, "IDEMPOTENCY_CONFLICT", err.Error())
			return
		}
		writeDirectPrintError(c, http.StatusInternalServerError, "PRINT_JOB_PERSIST_FAILED", "Failed to save print job.")
		return
	}
	c.JSON(http.StatusAccepted, gin.H{"status": "accepted", "duplicate": duplicate, "job": job})
}

func writeDirectPrintError(c *gin.Context, status int, code, message string) {
	c.JSON(status, gin.H{"status": "error", "code": code, "error": message, "message": message})
}

func createManualTextJob(c *gin.Context, service *printing.Service, availability *printerAvailability) {
	var request webLabelPrintRequest
	if err := c.ShouldBindJSON(&request); err != nil {
		c.JSON(http.StatusBadRequest, gin.H{"code": "INVALID_LABEL_REQUEST", "message": "标签打印参数无效。"})
		return
	}

	request.TemplateID = firstNonEmpty(request.TemplateID, request.TemplateCode)
	request.Text = firstNonEmpty(request.Text, request.LabelContent)
	request.Prefix = firstNonEmpty(request.Prefix, request.LabelPrefix)
	if request.TemplateID == "" || request.Text == "" || len([]rune(request.Text)) > 2000 || len([]rune(request.Prefix)) > 20 {
		c.JSON(http.StatusBadRequest, gin.H{"code": "INVALID_LABEL_REQUEST", "message": "请检查标签模板、内容和前缀长度。"})
		return
	}
	if request.Copies < 1 {
		request.Copies = 1
	}
	if request.Copies > 100 {
		c.JSON(http.StatusBadRequest, gin.H{"code": "INVALID_LABEL_COPIES", "message": "打印份数必须在 1 到 100 之间。"})
		return
	}

	var selected *printing.Template
	for _, template := range availableLabelTemplates(service.Templates(), availability) {
		if template.ID == request.TemplateID {
			selected = &template
			break
		}
	}
	if selected == nil {
		c.JSON(http.StatusConflict, gin.H{"code": "LABEL_TEMPLATE_UNAVAILABLE", "message": "所选标签模板或打印机当前不可用。"})
		return
	}
	if message := validateRestrictedLabelText(*selected, request.Text); message != "" {
		c.JSON(http.StatusBadRequest, gin.H{"code": "LABEL_TEXT_TOO_LONG", "message": message})
		return
	}

	qrPayload := request.Prefix + request.Text
	job, duplicate, err := service.Create(printing.Request{
		Source:        "web",
		TemplateID:    selected.ID,
		Kind:          "manual_text",
		Text:          request.Text,
		QRCodeContent: qrPayload,
		Copies:        request.Copies,
		Items: []printing.Item{{
			SKUCode:       request.Text,
			QRCodeContent: qrPayload,
			Quantity:      request.Copies,
		}},
	})
	if err != nil {
		c.JSON(http.StatusInternalServerError, gin.H{"code": "PRINT_JOB_PERSIST_FAILED", "message": "打印历史保存失败。"})
		return
	}
	c.JSON(http.StatusAccepted, gin.H{"status": "accepted", "duplicate": duplicate, "job": job})
}

func firstNonEmpty(values ...string) string {
	for _, value := range values {
		if value = strings.TrimSpace(value); value != "" {
			return value
		}
	}
	return ""
}
