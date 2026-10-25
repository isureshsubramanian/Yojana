import { pageSizeChoices } from "./gridPreferences";

export function PageSizeSelect({ value, defaultSize, options, onChange }: {
  value: number; defaultSize: number; options?: readonly number[] | undefined; onChange: (size: number) => void;
}) {
  return <label className="page-size"><span>Rows per page</span><select className="select" aria-label="Rows per page" value={value}
    onChange={event => onChange(Number(event.target.value))}>
    {pageSizeChoices(defaultSize, options).map(size => <option value={size} key={size}>{size}</option>)}
  </select></label>;
}
