# PDF Ingestion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Allow procurement users to upload basic text-based PDF purchase orders and parse them into the existing canonical order model.

**Architecture:** Add a `PdfOrderParser` in `ProcuLink.Transform` using PdfPig text extraction, register it in API DI, and allow `.pdf` through the existing upload endpoint. Keep the first parser intentionally conservative: text PDFs with recognizable header labels and line rows; scanned/OCR PDFs remain future work.

**Tech Stack:** .NET 8, `PdfPig` NuGet package (`UglyToad.PdfPig` namespace), xUnit/FluentAssertions, Next.js/Tailwind frontend file acceptance.

---

### Task 1: Parser Tests

**Files:**
- Create: `ProcuLink.Transform.Tests/Parsing/PdfOrderParserTests.cs`

- [x] Add tests for `.pdf` support and basic text purchase-order parsing.
- [x] Run the parser tests and verify they fail because `PdfOrderParser` does not exist yet.

### Task 2: Parser Implementation

**Files:**
- Modify: `ProcuLink.Transform/ProcuLink.Transform.csproj`
- Create: `ProcuLink.Transform/Parsing/PdfOrderParser.cs`

- [x] Add `PdfPig`.
- [x] Extract text per page, normalize lines, parse known header labels, and parse line rows with quantity/unit/price at the end.
- [x] Return empty lines for PDFs with no extractable text instead of throwing.
- [x] Run transform tests.

### Task 3: API Upload Wiring

**Files:**
- Modify: `ProcuLink.Api/Program.cs`
- Modify: `ProcuLink.Api/Controllers/OrdersController.cs`

- [x] Register `PdfOrderParser`.
- [x] Allow `.pdf` in upload validation and update the upload summary.
- [x] Run backend build/tests.

### Task 4: Frontend File Acceptance

**Files:**
- Modify: `project-proculink/src/components/orders/FileUploadZone.tsx`

- [x] Allow PDF MIME/extension in the file picker and drop validation.
- [x] Update upload copy to mention PDF.
- [x] Run frontend build.

### Task 5: Handoff

**Files:**
- Modify: `STATUS.md`
- Modify: `CLAUDE.md`
- Modify: `AGENTS.md`

- [x] Mark Group F implemented and Group G next.
- [x] Note limitation: text-based PDFs only; scanned/OCR PDFs deferred.
