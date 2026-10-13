import Link from '@docusaurus/Link';
import styles from './AudienceCards.module.css';

const audiences = [
  {
    to: '/guide/',
    title: 'Using Lucia',
    who: 'You run a home lab and are newer to computers or servers.',
    what: 'Plain steps to set Lucia up, add apps, and keep them healthy.',
  },
  {
    to: '/deploy/',
    title: 'Deploying Lucia',
    who: 'You manage the machines, network and containers Lucia runs on.',
    what: 'How the Spark, nodes, identity, DNS and updates fit together and get deployed.',
  },
  {
    to: '/code/',
    title: 'Building Lucia',
    who: 'You change the code.',
    what: 'How each part is built, where to find it, and the reasoning behind the design.',
  },
];

/** The three doorways on the docs home page. */
export default function AudienceCards() {
  return (
    <div className={styles.grid}>
      {audiences.map(item => (
        <Link key={item.to} to={item.to} className={styles.card}>
          <h3>{item.title}</h3>
          <p>{item.who}</p>
          <p className={styles.what}>{item.what}</p>
        </Link>
      ))}
    </div>
  );
}
