codeunit 50013 "Yojana Tracked Document Post"
{
    procedure Post(var Request: Record "Yojana Tracking Request"; DocumentType: Text; DocumentId: Guid; LogicalReference: Text; Metadata: JsonObject; Tracking: JsonArray; var ActionContext: WebServiceActionContext)
    var
        PostedDocumentId: Guid;
        PostedDocumentNumber: Code[20];
    begin
        CheckRequest(Request, DocumentType, DocumentId, LogicalReference, Tracking);
        if FindPostedResult(Request, DocumentId) then begin
            SetActionResponse(Request, ActionContext);
            exit;
        end;

        case DocumentType of
            'salesShipment':
                PostSalesShipment(DocumentId, LogicalReference, Metadata, PostedDocumentId, PostedDocumentNumber);
            'salesCreditMemo':
                PostSalesCreditMemo(DocumentId, PostedDocumentId, PostedDocumentNumber);
            'purchaseReceipt':
                PostPurchaseReceipt(DocumentId, LogicalReference, Metadata, PostedDocumentId, PostedDocumentNumber);
            'transferShipment':
                PostTransferShipment(DocumentId, LogicalReference, Metadata, PostedDocumentId, PostedDocumentNumber);
            'transferReceipt':
                PostTransferReceipt(DocumentId, PostedDocumentId, PostedDocumentNumber);
            'returnReceipt':
                PostReturnReceipt(DocumentId, LogicalReference, Metadata, PostedDocumentId, PostedDocumentNumber);
            'serviceOrder':
                CreateServiceOrder(DocumentId, LogicalReference, Metadata, PostedDocumentId, PostedDocumentNumber);
            else
                Error(UnsupportedDocumentTypeErr, DocumentType);
        end;

        SavePostedResult(Request, DocumentId, LogicalReference, PostedDocumentId, PostedDocumentNumber);
        SetActionResponse(Request, ActionContext);
    end;

    local procedure CheckRequest(Request: Record "Yojana Tracking Request"; DocumentType: Text; var DocumentId: Guid; var LogicalReference: Text; Tracking: JsonArray)
    var
        Requests: Record "Yojana Tracking Request";
    begin
        if IsNullGuid(DocumentId) then
            DocumentId := Request."Source Id";
        if LogicalReference = '' then
            LogicalReference := Format(DocumentId, 0, 4);
        if Request."Source Id" <> DocumentId then
            Error(DocumentIdMismatchErr);
        if Request."Source Type" <> DocumentType then
            Error(DocumentTypeMismatchErr);
        Requests.SetRange("Source Id", DocumentId);
        Requests.SetRange("Source Type", DocumentType);
        if Requests.IsEmpty() then
            Error(TrackingRequiredErr);
        if Tracking.Count() <> Requests.Count() then
            Error(TrackingCountMismatchErr);
    end;

    local procedure FindPostedResult(var Request: Record "Yojana Tracking Request"; DocumentId: Guid): Boolean
    begin
        Request.Reset();
        Request.SetRange("Source Id", DocumentId);
        Request.SetFilter("Posted At", '<>%1', 0DT);
        exit(Request.FindFirst());
    end;

    local procedure PostSalesShipment(DocumentId: Guid; LogicalReference: Text; Metadata: JsonObject; var PostedDocumentId: Guid; var PostedDocumentNumber: Code[20])
    var
        SalesHeader: Record "Sales Header";
        SalesShipmentHeader: Record "Sales Shipment Header";
    begin
        SalesHeader.Init();
        SalesHeader."Document Type" := SalesHeader."Document Type"::Order;
        SalesHeader.SystemId := DocumentId;
        SalesHeader.Insert(true, true);
        SalesHeader.Validate("Sell-to Customer No.", MetadataText(Metadata, 'customerNumber'));
        SalesHeader.Validate("Ship-to Code", MetadataText(Metadata, 'shipToCode'));
        SalesHeader.Validate("External Document No.", CopyStr(LogicalReference, 1, MaxStrLen(SalesHeader."External Document No.")));
        SalesHeader.Modify(true);
        CreateSalesLinesAndTracking(DocumentId, SalesHeader, false);
        SalesHeader.Ship := true;
        SalesHeader.Invoice := false;
        SalesHeader.SendToPosting(Codeunit::"Sales-Post");

        SalesShipmentHeader.SetRange("Order No.", SalesHeader."No.");
        SalesShipmentHeader.FindLast();
        PostedDocumentId := SalesShipmentHeader.SystemId;
        PostedDocumentNumber := SalesShipmentHeader."No.";
    end;

    local procedure PostSalesCreditMemo(DocumentId: Guid; var PostedDocumentId: Guid; var PostedDocumentNumber: Code[20])
    var
        SalesHeader: Record "Sales Header";
        SalesCrMemoHeader: Record "Sales Cr.Memo Header";
    begin
        SalesHeader.SetRange(SystemId, DocumentId);
        SalesHeader.SetRange("Document Type", SalesHeader."Document Type"::"Credit Memo");
        SalesHeader.FindFirst();
        CreateSalesLinesAndTracking(DocumentId, SalesHeader, true);
        SalesHeader.Receive := true;
        SalesHeader.Invoice := true;
        SalesHeader.SendToPosting(Codeunit::"Sales-Post");

        SalesCrMemoHeader.SetRange("Pre-Assigned No.", SalesHeader."No.");
        SalesCrMemoHeader.FindLast();
        PostedDocumentId := SalesCrMemoHeader.SystemId;
        PostedDocumentNumber := SalesCrMemoHeader."No.";
    end;

    local procedure PostPurchaseReceipt(DocumentId: Guid; LogicalReference: Text; Metadata: JsonObject; var PostedDocumentId: Guid; var PostedDocumentNumber: Code[20])
    var
        PurchaseHeader: Record "Purchase Header";
        PurchRcptHeader: Record "Purch. Rcpt. Header";
    begin
        PurchaseHeader.SetRange(SystemId, DocumentId);
        PurchaseHeader.SetRange("Document Type", PurchaseHeader."Document Type"::Order);
        if not PurchaseHeader.FindFirst() then begin
            PurchaseHeader.Init();
            PurchaseHeader."Document Type" := PurchaseHeader."Document Type"::Order;
            PurchaseHeader.SystemId := DocumentId;
            PurchaseHeader.Insert(true, true);
            PurchaseHeader.Validate("Buy-from Vendor No.", MetadataText(Metadata, 'vendorNumber'));
            PurchaseHeader.Validate("Vendor Order No.", CopyStr(LogicalReference, 1, MaxStrLen(PurchaseHeader."Vendor Order No.")));
            PurchaseHeader.Modify(true);
            CreatePurchaseLinesAndTracking(DocumentId, PurchaseHeader);
        end else
            CreatePurchaseTracking(DocumentId, PurchaseHeader);
        PurchaseHeader.Receive := true;
        PurchaseHeader.Invoice := false;
        PurchaseHeader.SendToPosting(Codeunit::"Purch.-Post");

        PurchRcptHeader.SetRange("Order No.", PurchaseHeader."No.");
        PurchRcptHeader.FindLast();
        PostedDocumentId := PurchRcptHeader.SystemId;
        PostedDocumentNumber := PurchRcptHeader."No.";
    end;

    local procedure PostTransferShipment(DocumentId: Guid; LogicalReference: Text; Metadata: JsonObject; var PostedDocumentId: Guid; var PostedDocumentNumber: Code[20])
    var
        TransferHeader: Record "Transfer Header";
        TransferShipmentHeader: Record "Transfer Shipment Header";
        PostShipment: Codeunit "TransferOrder-Post Shipment";
    begin
        TransferHeader.Init();
        TransferHeader.SystemId := DocumentId;
        TransferHeader.Insert(true, true);
        TransferHeader.Validate("Transfer-from Code", MetadataText(Metadata, 'fromLocationCode'));
        TransferHeader.Validate("Transfer-to Code", MetadataText(Metadata, 'toLocationCode'));
        TransferHeader.Validate("External Document No.", CopyStr(LogicalReference, 1, MaxStrLen(TransferHeader."External Document No.")));
        TransferHeader.Modify(true);
        CreateTransferLinesAndTracking(DocumentId, TransferHeader, false);
        PostShipment.Run(TransferHeader);

        TransferShipmentHeader.SetRange("Transfer Order No.", TransferHeader."No.");
        TransferShipmentHeader.FindLast();
        PostedDocumentId := TransferShipmentHeader.SystemId;
        PostedDocumentNumber := TransferShipmentHeader."No.";
    end;

    local procedure PostTransferReceipt(DocumentId: Guid; var PostedDocumentId: Guid; var PostedDocumentNumber: Code[20])
    var
        TransferHeader: Record "Transfer Header";
        TransferReceiptHeader: Record "Transfer Receipt Header";
        PostReceipt: Codeunit "TransferOrder-Post Receipt";
    begin
        TransferHeader.SetRange(SystemId, DocumentId);
        TransferHeader.FindFirst();
        CreateTransferLinesAndTracking(DocumentId, TransferHeader, true);
        PostReceipt.Run(TransferHeader);

        TransferReceiptHeader.SetRange("Transfer Order No.", TransferHeader."No.");
        TransferReceiptHeader.FindLast();
        PostedDocumentId := TransferReceiptHeader.SystemId;
        PostedDocumentNumber := TransferReceiptHeader."No.";
    end;

    local procedure PostReturnReceipt(DocumentId: Guid; LogicalReference: Text; Metadata: JsonObject; var PostedDocumentId: Guid; var PostedDocumentNumber: Code[20])
    var
        SalesHeader: Record "Sales Header";
        ReturnReceiptHeader: Record "Return Receipt Header";
    begin
        SalesHeader.Init();
        SalesHeader."Document Type" := SalesHeader."Document Type"::"Return Order";
        SalesHeader.SystemId := DocumentId;
        SalesHeader.Insert(true, true);
        SalesHeader.Validate("Sell-to Customer No.", MetadataText(Metadata, 'customerNumber'));
        SalesHeader.Validate("External Document No.", CopyStr(LogicalReference, 1, MaxStrLen(SalesHeader."External Document No.")));
        SalesHeader.Modify(true);
        CreateSalesLinesAndTracking(DocumentId, SalesHeader, true);
        SalesHeader.Receive := true;
        SalesHeader.Invoice := false;
        SalesHeader.SendToPosting(Codeunit::"Sales-Post");

        ReturnReceiptHeader.SetRange("Return Order No.", SalesHeader."No.");
        ReturnReceiptHeader.FindLast();
        PostedDocumentId := ReturnReceiptHeader.SystemId;
        PostedDocumentNumber := ReturnReceiptHeader."No.";
    end;

    local procedure CreateServiceOrder(DocumentId: Guid; LogicalReference: Text; Metadata: JsonObject; var PostedDocumentId: Guid; var PostedDocumentNumber: Code[20])
    var
        ServiceHeader: Record "Service Header";
        ServiceItem: Record "Service Item";
    begin
        ServiceItem.Init();
        ServiceItem."No." := CopyStr(LogicalReference, 1, MaxStrLen(ServiceItem."No."));
        ServiceItem.SystemId := DocumentId;
        ServiceItem.Validate("Item No.", MetadataText(Metadata, 'itemNumber'));
        ServiceItem.Validate("Customer No.", MetadataText(Metadata, 'customerNumber'));
        ServiceItem.Validate("Serial No.", MetadataText(Metadata, 'serialNumber'));
        ServiceItem.Insert(true, true);

        ServiceHeader.Init();
        ServiceHeader."Document Type" := ServiceHeader."Document Type"::Order;
        ServiceHeader.Insert(true);
        ServiceHeader.Validate("Customer No.", MetadataText(Metadata, 'customerNumber'));
        ServiceHeader.Validate(Description, CopyStr(MetadataText(Metadata, 'description'), 1, MaxStrLen(ServiceHeader.Description)));
        ServiceHeader.Modify(true);
        PostedDocumentId := ServiceItem.SystemId;
        PostedDocumentNumber := ServiceItem."No.";
    end;

    local procedure CreateSalesLinesAndTracking(DocumentId: Guid; SalesHeader: Record "Sales Header"; Positive: Boolean)
    var
        Request: Record "Yojana Tracking Request";
        SalesLine: Record "Sales Line";
    begin
        Request.SetRange("Source Id", DocumentId);
        if not Request.FindSet() then
            Error(TrackingRequiredErr);
        repeat
            SalesLine.SetRange("Document Type", SalesHeader."Document Type");
            SalesLine.SetRange("Document No.", SalesHeader."No.");
            SalesLine.SetRange("Line No.", Request."Source Line Number");
            if not SalesLine.FindFirst() then begin
                SalesLine.Init();
                SalesLine."Document Type" := SalesHeader."Document Type";
                SalesLine."Document No." := SalesHeader."No.";
                SalesLine."Line No." := Request."Source Line Number";
                SalesLine.Validate(Type, SalesLine.Type::Item);
                SalesLine.Validate("No.", Request."Item Number");
                SalesLine.Validate("Location Code", Request."Location Code");
                SalesLine.Validate(Quantity, Request.Quantity);
                SalesLine.Insert(true);
            end;
            CreateTracking(Request, Database::"Sales Line", SalesLine."Document Type".AsInteger(), SalesHeader."No.", SalesLine."Line No.", Positive);
        until Request.Next() = 0;
    end;

    local procedure CreatePurchaseLinesAndTracking(DocumentId: Guid; PurchaseHeader: Record "Purchase Header")
    var
        Request: Record "Yojana Tracking Request";
        PurchaseLine: Record "Purchase Line";
    begin
        Request.SetRange("Source Id", DocumentId);
        if not Request.FindSet() then
            Error(TrackingRequiredErr);
        repeat
            PurchaseLine.Init();
            PurchaseLine."Document Type" := PurchaseHeader."Document Type";
            PurchaseLine."Document No." := PurchaseHeader."No.";
            PurchaseLine."Line No." := Request."Source Line Number";
            PurchaseLine.Validate(Type, PurchaseLine.Type::Item);
            PurchaseLine.Validate("No.", Request."Item Number");
            PurchaseLine.Validate("Location Code", Request."Location Code");
            PurchaseLine.Validate(Quantity, Request.Quantity);
            PurchaseLine.Insert(true);
            CreateTracking(Request, Database::"Purchase Line", PurchaseLine."Document Type".AsInteger(), PurchaseHeader."No.", PurchaseLine."Line No.", true);
        until Request.Next() = 0;
    end;

    local procedure CreatePurchaseTracking(DocumentId: Guid; PurchaseHeader: Record "Purchase Header")
    var
        Request: Record "Yojana Tracking Request";
    begin
        Request.SetRange("Source Id", DocumentId);
        if not Request.FindSet() then
            Error(TrackingRequiredErr);
        repeat
            CreateTracking(Request, Database::"Purchase Line", PurchaseHeader."Document Type".AsInteger(), PurchaseHeader."No.", Request."Source Line Number", true);
        until Request.Next() = 0;
    end;

    local procedure CreateTransferLinesAndTracking(DocumentId: Guid; TransferHeader: Record "Transfer Header"; Positive: Boolean)
    var
        Request: Record "Yojana Tracking Request";
        TransferLine: Record "Transfer Line";
    begin
        Request.SetRange("Source Id", DocumentId);
        if not Request.FindSet() then
            Error(TrackingRequiredErr);
        repeat
            TransferLine.SetRange("Document No.", TransferHeader."No.");
            TransferLine.SetRange("Line No.", Request."Source Line Number");
            if not TransferLine.FindFirst() then begin
                TransferLine.Init();
                TransferLine."Document No." := TransferHeader."No.";
                TransferLine."Line No." := Request."Source Line Number";
                TransferLine.Validate("Item No.", Request."Item Number");
                TransferLine.Validate(Quantity, Request.Quantity);
                TransferLine.Insert(true);
            end;
            CreateTracking(Request, Database::"Transfer Line", 0, TransferHeader."No.", TransferLine."Line No.", Positive);
        until Request.Next() = 0;
    end;

    local procedure CreateTracking(Request: Record "Yojana Tracking Request"; SourceType: Integer; SourceSubtype: Integer; SourceId: Code[20]; SourceLineNumber: Integer; Positive: Boolean)
    var
        CreateReservEntry: Codeunit "Create Reserv. Entry";
        ForReservEntry: Record "Reservation Entry";
        QuantityBase: Decimal;
    begin
        QuantityBase := Abs(Request."Quantity Base");
        if QuantityBase = 0 then
            QuantityBase := Abs(Request.Quantity);
        if not Positive then
            QuantityBase := -QuantityBase;
        ForReservEntry.Init();
        ForReservEntry."Serial No." := Request."Serial Number";
        ForReservEntry."Lot No." := Request."Lot Number";
        ForReservEntry."Package No." := Request."Package Number";
        ForReservEntry.Positive := Positive;
        CreateReservEntry.CreateReservEntryFor(SourceType, SourceSubtype, SourceId, '', 0, SourceLineNumber, 1, QuantityBase, QuantityBase, ForReservEntry);
        CreateReservEntry.CreateEntry(Request."Item Number", '', Request."Location Code", '', 0D, 0D, 0, Enum::"Reservation Status"::Surplus);
    end;

    local procedure SavePostedResult(var Request: Record "Yojana Tracking Request"; DocumentId: Guid; LogicalReference: Text; PostedDocumentId: Guid; PostedDocumentNumber: Code[20])
    begin
        Request.Reset();
        Request.SetRange("Source Id", DocumentId);
        Request.FindSet(true);
        repeat
            Request."Logical Reference" := CopyStr(LogicalReference, 1, MaxStrLen(Request."Logical Reference"));
            Request."Posted Document Id" := PostedDocumentId;
            Request."Posted Document Number" := PostedDocumentNumber;
            Request."Posted At" := CurrentDateTime();
            Request.Modify();
        until Request.Next() = 0;
        Request.FindFirst();
    end;

    local procedure SetActionResponse(Request: Record "Yojana Tracking Request"; var ActionContext: WebServiceActionContext)
    begin
        ActionContext.SetObjectType(ObjectType::Page);
        ActionContext.SetObjectId(Page::"Yojana Post Result API");
        ActionContext.AddEntityKey(Request.FieldNo(SystemId), Request.SystemId);
        ActionContext.SetResultCode(WebServiceActionResultCode::Created);
    end;

    local procedure MetadataText(Metadata: JsonObject; Name: Text): Text
    var
        Value: JsonToken;
    begin
        if Metadata.Get(Name, Value) and Value.IsValue() then
            exit(Value.AsValue().AsText());
        exit('');
    end;

    var
        DocumentIdMismatchErr: Label 'documentId does not match the bound tracking request.';
        DocumentTypeMismatchErr: Label 'documentType does not match the bound tracking request.';
        TrackingRequiredErr: Label 'At least one tracking assignment is required.';
        TrackingCountMismatchErr: Label 'tracking must match the staged tracking assignments.';
        UnsupportedDocumentTypeErr: Label 'The document type %1 is not supported.', Comment = '%1 is the documentType value.';
}
