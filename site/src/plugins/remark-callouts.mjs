// GitHub alert syntax (> [!NOTE]) becomes an engraved-label callout.
const LABELS = {
  NOTE: ['note', 'Note'],
  TIP: ['note', 'Tip'],
  IMPORTANT: ['requires', 'Requires'],
  WARNING: ['caution', 'Caution'],
  CAUTION: ['destructive', 'Destructive'],
};

export default function remarkCallouts() {
  const walk = (node) => {
    if (node.type === 'blockquote') {
      const para = node.children?.[0];
      const text = para?.type === 'paragraph' ? para.children?.[0] : null;
      const m = text?.type === 'text' && text.value.match(/^\[!(NOTE|TIP|IMPORTANT|WARNING|CAUTION)\][ \t]*\n?/);
      if (m) {
        const [kind, label] = LABELS[m[1]];
        text.value = text.value.slice(m[0].length);
        if (!text.value) {
          para.children.shift();
          if (para.children[0]?.type === 'break') para.children.shift();
          if (!para.children.length) node.children.shift();
        }
        node.data = { hName: 'aside', hProperties: { className: ['callout', `callout-${kind}`] } };
        node.children.unshift({
          type: 'paragraph',
          data: { hName: 'p', hProperties: { className: ['callout-label'] } },
          children: [{ type: 'text', value: label }],
        });
      }
    }
    node.children?.forEach(walk);
  };
  return (tree) => walk(tree);
}
