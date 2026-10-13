import type {ReactNode} from 'react';
import useBaseUrl from '@docusaurus/useBaseUrl';
import styles from './Screenshot.module.css';

type Props = {name: string; alt: string; children?: ReactNode};

/** A screenshot from static/img/shots, with a caption. Keep alt text about what the picture shows, not that it is a picture. */
export default function Screenshot({name, alt, children}: Props) {
  const src = useBaseUrl(`/img/shots/${name}.png`);
  return (
    <figure className={styles.shot}>
      <img src={src} alt={alt} loading="lazy" />
      {children && <figcaption>{children}</figcaption>}
    </figure>
  );
}
