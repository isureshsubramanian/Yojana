page 50004 "Yojana Tracking Specs API"
{
    PageType = API;
    APIPublisher = 'yojana';
    APIGroup = 'integration';
    APIVersion = 'v1.0';
    EntityName = 'trackingSpecification';
    EntitySetName = 'trackingSpecifications';
    EntityCaption = 'Tracking Specification';
    EntitySetCaption = 'Tracking Specifications';
    SourceTable = "Tracking Specification";
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
                field(itemNumber; Rec."Item No.") { Caption = 'Item Number'; Editable = false; }
                field(locationCode; Rec."Location Code") { Caption = 'Location Code'; Editable = false; }
                field(quantity; Rec.Quantity) { Caption = 'Quantity'; Editable = false; }
                field(quantityBase; Rec."Quantity (Base)") { Caption = 'Quantity Base'; Editable = false; }
                field(sourceType; SourceType) { Caption = 'Source Type'; Editable = false; }
                field(sourceSubtype; SourceSubtype) { Caption = 'Source Subtype'; Editable = false; }
                field(sourceId; SourceId) { Caption = 'Source Id'; Editable = false; }
                field(sourceLineNumber; Rec."Source Ref. No.") { Caption = 'Source Line Number'; Editable = false; }
                field(serialNumber; Rec."Serial No.") { Caption = 'Serial Number'; Editable = false; }
                field(lotNumber; Rec."Lot No.") { Caption = 'Lot Number'; Editable = false; }
                field(packageNumber; Rec."Package No.") { Caption = 'Package Number'; Editable = false; }
                field(lastModifiedDateTime; Rec.SystemModifiedAt) { Caption = 'Last Modified Date Time'; Editable = false; }
            }
        }
    }

    trigger OnAfterGetRecord()
    begin
        SourceType := Format(Rec."Source Type", 0, 9);
        SourceSubtype := Format(Rec."Source Subtype", 0, 9);
        Clear(SourceId);
        if not Evaluate(SourceId, Rec."Source ID") then
            SourceId := Rec.SystemId;
    end;

    var
        SourceId: Guid;
        SourceType: Text;
        SourceSubtype: Text;
}
