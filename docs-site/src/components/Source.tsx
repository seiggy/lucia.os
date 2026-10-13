import type {ReactNode} from 'react';

const repo = 'https://github.com/seiggy/lucia.os/blob/main/';

type Props = {path: string; line?: number; children?: ReactNode};

/** Links a path in the repo, so a reader can jump from the explanation to the code. Pass the repo-relative path with forward slashes. */
export default function Source({path, line, children}: Props) {
  return (
    <a href={`${repo}${path}${line ? `#L${line}` : ''}`}>
      <code>{children ?? path}</code>
    </a>
  );
}
