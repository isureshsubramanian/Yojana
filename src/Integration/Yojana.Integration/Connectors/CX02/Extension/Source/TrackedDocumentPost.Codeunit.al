codeunit 50013 "Yojana Tracked Document Post"
{
    procedure Post(var Request: Record "Yojana Tracking Request"; DocumentType: Text; DocumentId: Guid; LogicalReference: Text; Metadata: JsonObject; Tracking: JsonArray; var ActionContext: WebServiceActionContext)
    var
        PostedDocumentId: Guid;
        PostedDocumentNumber: Code[20];
        FailureCode: Text;
        FailureMessage: Text;
    begin
        CheckRequest(DocumentType, DocumentId, LogicalReference, Tracking);
        if FindPostedResult(Request, LogicalReference) then begin
            SetActionResponse(Request, ActionContext);
            exit;
        end;
        StageRequest(Request, DocumentType, DocumentId, LogicalReference);
        StageTracking(DocumentType, DocumentId, Metadata, Tracking);
        Commit();

        if not PostNative(DocumentType, DocumentId, LogicalReference, Metadata,
            PostedDocumentId, PostedDocumentNumber) then begin
            FailureCode := GetLastErrorCode();
            FailureMessage := GetLastErrorText();
            if FailureCode = '' then
                FailureCode := 'postingFailed';
            SaveFailure(Request, FailureCode);
            Commit();
            Error(FailureMessage);
        end;

        SavePostedResult(Request, DocumentType, PostedDocumentId, PostedDocumentNumber);
        SetActionResponse(Request, ActionContext);
    end;

    [TryFunction]
    local procedure PostNative(DocumentType: Text; DocumentId: Guid; LogicalReference: Text; Metadata: JsonObject; var PostedDocumentId: Guid; var PostedDocumentNumber: Code[20])
    begin
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
            'itemJournal':
                PostItemJournal(DocumentId, LogicalReference, Metadata, PostedDocumentId, PostedDocumentNumber);
            'serviceOrder':
                CreateServiceOrder(DocumentId, LogicalReference, Metadata, PostedDocumentId, PostedDocumentNumber);
            else
                Error(UnsupportedDocumentTypeErr, DocumentType);
        end;
    end;

    local procedure CheckRequest(DocumentType: Text; DocumentId: Guid; LogicalReference: Text; Tracking: JsonArray)
    begin
        if IsNullGuid(DocumentId) then
            Error(DocumentIdRequiredErr);
        if LogicalReference = '' then
            Error(LogicalReferenceRequiredErr);
        if StrLen(LogicalReference) > 100 then
            Error(LogicalReferenceTooLongErr);
        if Tracking.Count() = 0 then
            Error(TrackingRequiredErr);
    end;

    local procedure FindPostedResult(var Request: Record "Yojana Tracking Request"; LogicalReference: Text): Boolean
    begin
        Request.Reset();
        Request.SetRange("Is Request Header", true);
        Request.SetRange("Logical Reference", LogicalReference);
        Request.SetFilter(Status, '%1|%2', 'posted', 'created');
        exit(Request.FindFirst());
    end;

    local procedure StageRequest(var Request: Record "Yojana Tracking Request"; DocumentType: Text; DocumentId: Guid; LogicalReference: Text)
    begin
        Request.Reset();
        Request.SetRange("Is Request Header", true);
        Request.SetRange("Logical Reference", LogicalReference);
        if Request.FindFirst() then begin
            Request.Status := 'staged';
            Clear(Request."Failure Code");
            Request.Modify(true);
            exit;
        end;
        Request.Init();
        Request.SystemId := DocumentId;
        Request."Is Request Header" := true;
        Request."Source Type" := CopyStr(DocumentType, 1, MaxStrLen(Request."Source Type"));
        Request."Source Id" := DocumentId;
        Request."Logical Reference" := CopyStr(LogicalReference, 1, MaxStrLen(Request."Logical Reference"));
        Request.Status := 'staged';
        Request.Insert(true, true);
    end;

    local procedure StageTracking(DocumentType: Text; DocumentId: Guid; Metadata: JsonObject; Tracking: JsonArray)
    var
        Request: Record "Yojana Tracking Request";
        TrackingObject: JsonObject;
        TrackingToken: JsonToken;
        Index: Integer;
    begin
        Request.SetRange("Source Id", DocumentId);
        Request.SetRange("Is Request Header", false);
        Request.DeleteAll(true);
        Request.Reset();
        for Index := 0 to Tracking.Count() - 1 do begin
            Tracking.Get(Index, TrackingToken);
            TrackingObject := TrackingToken.AsObject();
            Request.Init();
            Request."Source Type" := CopyStr(DocumentType, 1, MaxStrLen(Request."Source Type"));
            Request."Source Id" := DocumentId;
            Request."Source Line Number" := JsonInteger(TrackingObject, 'sourceLineNumber');
            Request."Item Number" := CopyStr(JsonText(TrackingObject, 'itemNumber'), 1, MaxStrLen(Request."Item Number"));
            Request.Quantity := JsonDecimal(TrackingObject, 'quantity');
            Request."Quantity Base" := Request.Quantity;
            Request."Location Code" := CopyStr(JsonText(TrackingObject, 'locationCode'), 1,
                MaxStrLen(Request."Location Code"));
            if Request."Location Code" = '' then
                Request."Location Code" := CopyStr(MetadataText(Metadata, 'locationCode'), 1,
                    MaxStrLen(Request."Location Code"));
            if Request."Location Code" = '' then
                Request."Location Code" := CopyStr(MetadataText(Metadata, 'fromLocationCode'), 1,
                    MaxStrLen(Request."Location Code"));
            Request."Serial Number" := CopyStr(JsonText(TrackingObject, 'serialNumber'), 1, MaxStrLen(Request."Serial Number"));
            Request."Lot Number" := CopyStr(JsonText(TrackingObject, 'lotNumber'), 1, MaxStrLen(Request."Lot Number"));
            Request."Package Number" := CopyStr(JsonText(TrackingObject, 'packageNumber'), 1, MaxStrLen(Request."Package Number"));
            Request.Insert(true);
        end;
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
        Request.SetRange("Is Request Header", false);
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
        Request.SetRange("Is Request Header", false);
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
        Request.SetRange("Is Request Header", false);
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
        Request.SetRange("Is Request Header", false);
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

    local procedure PostItemJournal(DocumentId: Guid; LogicalReference: Text; Metadata: JsonObject; var PostedDocumentId: Guid; var PostedDocumentNumber: Code[20])
    var
        Request: Record "Yojana Tracking Request";
        ItemJournalLine: Record "Item Journal Line";
        ItemLedgerEntry: Record "Item Ledger Entry";
        PostLine: Codeunit "Item Jnl.-Post Line";
    begin
        Request.SetRange("Source Id", DocumentId);
        Request.SetRange("Is Request Header", false);
        if not Request.FindSet() then
            Error(TrackingRequiredErr);
        repeat
            ItemJournalLine.Init();
            ItemJournalLine."Journal Template Name" := 'ITEM';
            ItemJournalLine."Journal Batch Name" := 'DEFAULT';
            ItemJournalLine."Line No." := Request."Source Line Number";
            ItemJournalLine."Posting Date" := Today();
            ItemJournalLine."Document No." := CopyStr(LogicalReference, 1, MaxStrLen(ItemJournalLine."Document No."));
            if Request.Quantity < 0 then
                ItemJournalLine."Entry Type" := ItemJournalLine."Entry Type"::"Negative Adjmt."
            else
                ItemJournalLine."Entry Type" := ItemJournalLine."Entry Type"::"Positive Adjmt.";
            ItemJournalLine.Validate("Item No.", Request."Item Number");
            ItemJournalLine.Validate("Location Code", MetadataText(Metadata, 'locationCode'));
            ItemJournalLine.Validate(Quantity, Abs(Request.Quantity));
            CreateTracking(Request, Database::"Item Journal Line", ItemJournalLine."Entry Type".AsInteger(),
                ItemJournalLine."Journal Batch Name", ItemJournalLine."Line No.", Request.Quantity > 0);
            PostLine.RunWithCheck(ItemJournalLine);
        until Request.Next() = 0;

        ItemLedgerEntry.SetRange("Document No.", CopyStr(LogicalReference, 1, MaxStrLen(ItemLedgerEntry."Document No.")));
        ItemLedgerEntry.FindLast();
        PostedDocumentId := ItemLedgerEntry.SystemId;
        PostedDocumentNumber := ItemLedgerEntry."Document No.";
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

    local procedure SavePostedResult(var Request: Record "Yojana Tracking Request"; DocumentType: Text; PostedDocumentId: Guid; PostedDocumentNumber: Code[20])
    begin
        Request."Posted Document Type" := CopyStr(DocumentType, 1, MaxStrLen(Request."Posted Document Type"));
        Request."Posted Document Id" := PostedDocumentId;
        Request."Posted Document Number" := PostedDocumentNumber;
        Request."Completed At" := CurrentDateTime();
        if DocumentType = 'serviceOrder' then
            Request.Status := 'created'
        else
            Request.Status := 'posted';
        Clear(Request."Failure Code");
        Request.Modify(true);
    end;

    local procedure SaveFailure(var Request: Record "Yojana Tracking Request"; FailureCode: Text)
    begin
        Request.Status := 'failed';
        Request."Failure Code" := CopyStr(FailureCode, 1, MaxStrLen(Request."Failure Code"));
        Request."Completed At" := CurrentDateTime();
        Request.Modify(true);
    end;

    local procedure SetActionResponse(Request: Record "Yojana Tracking Request"; var ActionContext: WebServiceActionContext)
    begin
        ActionContext.SetObjectType(ObjectType::Page);
        ActionContext.SetObjectId(Page::"Yojana Tracked Requests API");
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

    local procedure JsonText(Value: JsonObject; Name: Text): Text
    var
        Token: JsonToken;
    begin
        if Value.Get(Name, Token) and Token.IsValue() then
            exit(Token.AsValue().AsText());
        exit('');
    end;

    local procedure JsonInteger(Value: JsonObject; Name: Text): Integer
    var
        Token: JsonToken;
    begin
        if Value.Get(Name, Token) and Token.IsValue() then
            exit(Token.AsValue().AsInteger());
        exit(0);
    end;

    local procedure JsonDecimal(Value: JsonObject; Name: Text): Decimal
    var
        Token: JsonToken;
    begin
        if Value.Get(Name, Token) and Token.IsValue() then
            exit(Token.AsValue().AsDecimal());
        exit(0);
    end;

    var
        DocumentIdRequiredErr: Label 'documentId is required.';
        LogicalReferenceRequiredErr: Label 'logicalReference is required.';
        LogicalReferenceTooLongErr: Label 'logicalReference cannot exceed 100 characters.';
        TrackingRequiredErr: Label 'At least one tracking assignment is required.';
        UnsupportedDocumentTypeErr: Label 'The document type %1 is not supported.', Comment = '%1 is the documentType value.';
}
