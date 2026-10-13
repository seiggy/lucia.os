import type {ReactNode} from 'react';
import styles from './WhoWhatWhy.module.css';

type Props = {who: ReactNode; what: ReactNode; why: ReactNode};

/** Opens a page by saying who it is for, what it covers and why it exists. Every doc page starts with one. */
export default function WhoWhatWhy({who, what, why}: Props) {
  return (
    <dl className={styles.card}>
      <div><dt>Who it&rsquo;s for</dt><dd>{who}</dd></div>
      <div><dt>What it covers</dt><dd>{what}</dd></div>
      <div><dt>Why it exists</dt><dd>{why}</dd></div>
    </dl>
  );
}
