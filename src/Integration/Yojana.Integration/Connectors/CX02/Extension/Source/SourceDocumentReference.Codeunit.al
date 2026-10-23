codeunit 50015 "Yojana Source Document Ref"
{
    procedure Resolve(SourceType: Text; SourceDocumentNumber: Code[20]; var ContractType: Text; var DocumentId: Guid)
    begin
        Clear(DocumentId);
        ContractType := ContractSourceType(SourceType);
        ResolvePostedHeader(ContractType, SourceDocumentNumber, DocumentId);
    end;

    local procedure ContractSourceType(SourceType: Text): Text
    begin
        case LowerCase(DelChr(SourceType, '=', ' .-_')) of
            'salesinvoice': exit('salesInvoice');
            'salesshipment': exit('salesShipment');
            'salescreditmemo': exit('salesCreditMemo');
            'purchasereceipt', 'purchrcpt': exit('purchaseReceipt');
            'transfershipment': exit('transferShipment');
            'transferreceipt': exit('transferReceipt');
            'salesreturnreceipt', 'returnreceipt': exit('returnReceipt');
            'positiveadjmt', 'negativeadjmt', 'itemjournal': exit('itemJournal');
        end;
        exit('other');
    end;

    local procedure ResolvePostedHeader(ContractType: Text; SourceDocumentNumber: Code[20]; var DocumentId: Guid)
    var
        SalesInvoice: Record "Sales Invoice Header";
        SalesShipment: Record "Sales Shipment Header";
        SalesCreditMemo: Record "Sales Cr.Memo Header";
        PurchaseReceipt: Record "Purch. Rcpt. Header";
        TransferShipment: Record "Transfer Shipment Header";
        TransferReceipt: Record "Transfer Receipt Header";
        ReturnReceipt: Record "Return Receipt Header";
    begin
        case ContractType of
            'salesInvoice': if SalesInvoice.Get(SourceDocumentNumber) then DocumentId := SalesInvoice.SystemId;
            'salesShipment': if SalesShipment.Get(SourceDocumentNumber) then DocumentId := SalesShipment.SystemId;
            'salesCreditMemo': if SalesCreditMemo.Get(SourceDocumentNumber) then DocumentId := SalesCreditMemo.SystemId;
            'purchaseReceipt': if PurchaseReceipt.Get(SourceDocumentNumber) then DocumentId := PurchaseReceipt.SystemId;
            'transferShipment': if TransferShipment.Get(SourceDocumentNumber) then DocumentId := TransferShipment.SystemId;
            'transferReceipt': if TransferReceipt.Get(SourceDocumentNumber) then DocumentId := TransferReceipt.SystemId;
            'returnReceipt': if ReturnReceipt.Get(SourceDocumentNumber) then DocumentId := ReturnReceipt.SystemId;
        end;
    end;
}
