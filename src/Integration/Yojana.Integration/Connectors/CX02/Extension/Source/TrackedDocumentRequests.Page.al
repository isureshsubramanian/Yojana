page 50012 "Yojana Tracked Requests API"
{
    PageType = API;
    APIPublisher = 'yojana';
    APIGroup = 'integration';
    APIVersion = 'v1.0';
    EntityName = 'trackedDocumentRequest';
    EntitySetName = 'trackedDocumentRequests';
    EntityCaption = 'Tracked Document Request';
    EntitySetCaption = 'Tracked Document Requests';
    SourceTable = "Yojana Tracking Request";
    SourceTableView = where("Is Request Header" = const(true));
    ODataKeyFields = SystemId;
    DelayedInsert = true;
    Extensible = false;
    InsertAllowed = false;
    ModifyAllowed = false;
    DeleteAllowed = false;
    ChangeTrackingAllowed = true;

    layout
    {
        area(Content)
        {
            repeater(Entries)
            {
                field(id; Rec.SystemId) { Caption = 'Id'; Editable = false; }
                field(logicalReference; Rec."Logical Reference") { Caption = 'Logical Reference'; Editable = false; }
                field(status; Rec.Status) { Caption = 'Status'; Editable = false; }
                field(postedDocumentType; Rec."Posted Document Type") { Caption = 'Posted Document Type'; Editable = false; }
                field(postedDocumentId; Rec."Posted Document Id") { Caption = 'Posted Document Id'; Editable = false; }
                field(postedDocumentNumber; Rec."Posted Document Number") { Caption = 'Posted Document Number'; Editable = false; }
                field(completedAt; Rec."Completed At") { Caption = 'Completed At'; Editable = false; }
                field(failureCode; Rec."Failure Code") { Caption = 'Failure Code'; Editable = false; }
                field(lastModifiedDateTime; Rec.SystemModifiedAt) { Caption = 'Last Modified Date Time'; Editable = false; }
            }
        }
    }

    [ServiceEnabled]
    [Scope('Cloud')]
    procedure postTrackedDocument(documentType: Text; documentId: Guid; logicalReference: Text; metadata: JsonObject; tracking: JsonArray; var actionContext: WebServiceActionContext)
    var
        Posting: Codeunit "Yojana Tracked Document Post";
    begin
        Posting.Post(Rec, documentType, documentId, logicalReference, metadata, tracking, actionContext);
    end;
}
